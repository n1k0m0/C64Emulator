// SPDX-License-Identifier: Apache-2.0
using System.Text;
using System.Text.Json;
using C64Emulator.Core;
using OpenTK.Input;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: EmulatorHarness ROM_DIRECTORY OUTPUT_DIRECTORY [COMMANDS_JSON] [--raw-petscii]");
    return 2;
}
var romDirectory = Path.GetFullPath(args[0]);
var outputDirectory = Path.GetFullPath(args[1]);
var commands = args.Length > 2
    ? JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.GetFullPath(args[2])))
    : Array.Empty<string>();
bool rawPetscii = args.Skip(3).Contains("--raw-petscii", StringComparer.Ordinal);
Directory.CreateDirectory(outputDirectory);
Directory.SetCurrentDirectory(romDirectory);
foreach (var name in new[] { "c64-basic-kernal.bin", "c64-character.bin" })
{
    var resolved = RomPathResolver.ResolveRequired(name, romDirectory);
    if (Path.GetFullPath(resolved) != Path.Combine(romDirectory, name))
        throw new InvalidOperationException($"Unexpected ROM precedence: {resolved}");
}
var options = C64AccuracyOptions.Compatibility;
options.EnableLoadHack = false;
options.EnableKernalIecHooks = false;
options.EnableInputInjection = false;
using var c64 = new C64System(C64Model.Pal, options, romDirectory);
c64.LocalAudioEnabled = false;
var output = new StringBuilder();
var results = new List<object>();
var recent = new Queue<ushort>();
var watch = System.Diagnostics.Stopwatch.StartNew();
Run(1_200_000);
Snapshot("boot");
int sequence = 0;
foreach (var command in commands)
{
    output.Clear();
    int runCycles = 800_000;
    if (command.StartsWith("mount8:", StringComparison.Ordinal))
        output.AppendLine(c64.MountMedia(command[7..], 8));
    else if (command == "warm-reset:")
    {
        output.AppendLine(c64.WarmReset());
        runCycles = 1_200_000;
    }
    else if (command == "cold-reset:")
    {
        c64.Reset();
        output.AppendLine("COLD RESET");
        runCycles = 1_200_000;
    }
    else if (command.StartsWith("wait:", StringComparison.Ordinal))
        runCycles = int.Parse(command[5..]);
    else if (command.StartsWith("hold:", StringComparison.Ordinal))
    {
        int separator = command.IndexOf(':', 5);
        int cycles = int.Parse(command[5..separator]);
        Key key = Enum.Parse<Key>(command[(separator + 1)..], ignoreCase: true);
        c64.KeyboardKeyDown(key);
        Run(cycles);
        c64.KeyboardKeyUp(key);
        runCycles = 0;
    }
    else if (command.StartsWith("serpent-food:", StringComparison.Ordinal))
    {
        PlaySerpentToApple(int.Parse(command[13..]));
        runCycles = 0;
    }
    else if (command.StartsWith("run:", StringComparison.Ordinal))
    {
        int separator = command.IndexOf(':', 4);
        runCycles = int.Parse(command[4..separator]);
        c64.EnqueuePetsciiText(InputText(command[(separator + 1)..]) + "\r");
    }
    else if (command.StartsWith("keys:", StringComparison.Ordinal))
        TypeKeys(command[5..]);
    else
        c64.EnqueuePetsciiText(InputText(command.Replace("\\x14", "\u0014")) + "\r");
    Run(runCycles);
    Snapshot($"command-{++sequence:00}", command);
}
File.WriteAllText(Path.Combine(outputDirectory, "results.json"),
    JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Finished {sequence} commands in {watch.Elapsed.TotalSeconds:F2}s");
return 0;

void Run(int count)
{
    for (int i = 0; i < count; i++)
    {
        if (c64.Cpu.State == CpuState.Jammed)
        {
            Snapshot("jam");
            throw new InvalidOperationException($"CPU JAM at ${c64.Cpu.PC:X4}; recent: {string.Join(' ', recent.Select(x => x.ToString("X4")))}");
        }
        bool fetch = c64.Cpu.State == CpuState.FetchOpcode;
        bool capture = c64.Cpu.State == CpuState.FetchOpcode && c64.Cpu.PC == 0xFFD2 && (c64.PeekRam(0x009A) == 0 || c64.PeekRam(0x009A) == 3);
        byte value = c64.Cpu.A;
        c64.Tick();
        if (fetch && c64.Cpu.State == CpuState.ExecuteInstruction)
        {
            recent.Enqueue(c64.Cpu.LastOpcodeAddress);
            if (recent.Count > 48) recent.Dequeue();
        }
        if (capture && c64.Cpu.State == CpuState.ExecuteInstruction && c64.Cpu.LastOpcodeAddress == 0xFFD2)
        {
            if (value == 13) output.Append('\n');
            else if (value >= 65 && value <= 90 && (c64.Peek(0xD018) & 2) != 0)
                output.Append((char)(value + 32));
            else if (value >= 32 && value < 127) output.Append((char)value);
            else if (value >= 193 && value <= 218) output.Append((char)(value - 128));
            else if (value == 147) output.Append("[CLEAR]\n");
            else if (value == 20) output.Append("[DEL]");
        }
    }
}

string InputText(string text)
{
    if (rawPetscii) return text;
    return new string(text.Select(character => character switch
    {
        >= 'a' and <= 'z' => (char)(character - 32),
        >= 'A' and <= 'Z' => (char)(character + 128),
        _ => character
    }).ToArray());
}

// Test-only UI automation for the unchanged upstream SERPENT64 v1.0.4 PRG.
// Its documented screen map and zero-page state are READ ONLY here. Every
// turn is an ordinary physical keyboard event, processed by the real game.
// The game seeds food positions from its clock, so a fixed WASD sequence
// cannot reliably demonstrate scoring on every ROM build.
void PlaySerpentToApple(int cycleBudget)
{
    int spent = 0;
    int firstFoodCount = c64.PeekRam(0x002D);
    int startingLives = c64.PeekRam(0x0029);
    var turns = new StringBuilder();
    Key? held = null;
    int startX = c64.PeekRam(0x0013), startY = c64.PeekRam(0x0014);
    try
    {
        Advance(20_000); // Let the game's IRQ observe the preceding key release.
        while (c64.PeekRam(0x002D) == firstFoodCount)
        {
            if (c64.PeekRam(0x0012) != 3 || c64.PeekRam(0x0029) != startingLives)
                throw new InvalidOperationException("SERPENT64 left normal gameplay before eating an apple");
            int x = c64.PeekRam(0x0013), y = c64.PeekRam(0x0014);
            int direction = DirectionToFood(x, y, c64.PeekRam(0x0017));
            Key key = direction switch { 0 => Key.W, 1 => Key.D, 2 => Key.S, _ => Key.A };
            if (key != held)
            {
                if (held.HasValue) c64.KeyboardKeyUp(held.Value);
                c64.KeyboardKeyDown(key);
                held = key;
                turns.Append(key.ToString());
            }
            while (c64.PeekRam(0x0013) == x && c64.PeekRam(0x0014) == y
                   && c64.PeekRam(0x002D) == firstFoodCount)
                Advance(1_000);
            // Head coordinates are updated before all rendering is complete.
            // Wait one PAL frame before deciding the next physical turn.
            Advance(20_000);
        }
    }
    finally
    {
        if (held.HasValue) c64.KeyboardKeyUp(held.Value);
    }
    Advance(40_000);
    output.AppendLine($"Physical SERPENT64 route {turns}: ({startX},{startY}) -> "
        + $"({c64.PeekRam(0x0013)},{c64.PeekRam(0x0014)}), "
        + $"food {firstFoodCount}->{c64.PeekRam(0x002D)}, {spent} cycles");

    void Advance(int cycles)
    {
        if (spent + cycles > cycleBudget)
            throw new InvalidOperationException("Physical SERPENT64 apple route exceeded its cycle budget");
        Run(cycles);
        spent += cycles;
    }

    int DirectionToFood(int originX, int originY, int currentDirection)
    {
        var queue = new Queue<(int X, int Y, int First)>();
        var seen = new HashSet<(int, int)> { (originX, originY) };
        queue.Enqueue((originX, originY, -1));
        int[] dx = { 0, 1, 0, -1 }, dy = { -1, 0, 1, 0 };
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            for (int direction = 0; direction < 4; direction++)
            {
                if (node.First == -1 && direction == (currentDirection ^ 2)) continue;
                int x = node.X + dx[direction], y = node.Y + dy[direction];
                if (x < 0 || x >= 40 || y < 1 || y >= 25 || !seen.Add((x, y))) continue;
                byte cell = c64.PeekRam((ushort)(0x0400 + y * 40 + x));
                int first = node.First == -1 ? direction : node.First;
                if (cell == 0xB0) return first; // Actual food glyph.
                if (cell == 0x20) queue.Enqueue((x, y, first));
                // Walls, the snake and every other item remain obstacles.
            }
        }
        throw new InvalidOperationException("No reachable SERPENT64 apple in the observed screen map");
    }
}

void TypeKeys(string text)
{
    foreach (char original in text + "\r")
    {
        char character = char.ToUpperInvariant(original);
        bool shift = !rawPetscii && original is >= 'A' and <= 'Z';
        bool control = original == 3;
        Key key = character switch
        {
            ' ' => Key.Space,
            '\r' => Key.Enter,
            '\x14' => Key.BackSpace,
            '\x03' => Key.C,
            '/' => Key.Slash,
            '?' => Shifted(Key.Slash),
            '.' => Key.Period,
            '>' => Shifted(Key.Period),
            ',' => Key.Comma,
            '<' => Shifted(Key.Comma),
            '-' => Key.Minus,
            '_' => Shifted(Key.Minus),
            '\'' => Key.Quote,
            '"' => Shifted(Key.Quote),
            ';' => Key.Semicolon,
            ':' => Shifted(Key.Semicolon),
            '=' => Key.Plus,
            '+' => Shifted(Key.Plus),
            '!' => Shifted(Key.Number1),
            '@' => Shifted(Key.Number2),
            '#' => Shifted(Key.Number3),
            '$' => Shifted(Key.Number4),
            '%' => Shifted(Key.Number5),
            '^' => Shifted(Key.Number6),
            '&' => Shifted(Key.Number7),
            '*' => Shifted(Key.Number8),
            '(' => Shifted(Key.Number9),
            ')' => Shifted(Key.Number0),
            _ when character is >= 'A' and <= 'Z' => (Key)((int)Key.A + character - 'A'),
            _ when character is >= '0' and <= '9' => (Key)((int)Key.Number0 + character - '0'),
            _ => throw new ArgumentException($"No matrix key mapping: {character}")
        };
        if (shift) c64.KeyboardKeyDown(Key.ShiftLeft);
        if (control) c64.KeyboardKeyDown(Key.ControlLeft);
        c64.KeyboardKeyDown(key);
        Run(35_000);
        c64.KeyboardKeyUp(key);
        if (shift) c64.KeyboardKeyUp(Key.ShiftLeft);
        if (control) c64.KeyboardKeyUp(Key.ControlLeft);
        Run(25_000);

        Key Shifted(Key value) { shift = true; return value; }
    }
}

string Screen()
{
    var text = new StringBuilder();
    bool lower = (c64.Peek(0xD018) & 2) != 0;
    for (int row = 0; row < 25; row++)
    {
        var line = new StringBuilder();
        for (int column = 0; column < 40; column++)
        {
            byte value = (byte)(c64.PeekRam((ushort)(0x0400 + row * 40 + column)) & 0x7F);
            char character = value switch
            {
                0 => '@',
                >= 1 and <= 26 when lower => (char)(value + 96),
                < 32 => (char)(value + 64),
                < 64 => (char)value,
                >= 65 and <= 90 => (char)value,
                _ => '.'
            };
            line.Append(character);
        }
        text.AppendLine(line.ToString().TrimEnd());
    }
    return text.ToString();
}

void Snapshot(string label, string command = null)
{
    string screen = Screen();
    string transcript = output.ToString();
    Console.WriteLine($"--- {label}: {command} PC=${c64.Cpu.PC:X4} SP=${c64.Cpu.SP:X2} ---");
    Console.WriteLine(screen.TrimEnd());
    File.WriteAllText(Path.Combine(outputDirectory, label + ".txt"), screen);
    File.WriteAllText(Path.Combine(outputDirectory, label + "-stdout.txt"), transcript);
    var ram = new byte[65536];
    for (int address = 0; address < ram.Length; address++)
        ram[address] = c64.PeekRam((ushort)address);
    File.WriteAllBytes(Path.Combine(outputDirectory, label + "-ram.bin"), ram);
    results.Add(new {
        label, command, screen, transcript,
        pc = $"{c64.Cpu.PC:X4}", sp = $"{c64.Cpu.SP:X2}", state = c64.Cpu.State.ToString(),
        jiffies = (c64.PeekRam(0xA0) << 16) | (c64.PeekRam(0xA1) << 8) | c64.PeekRam(0xA2),
        keyboardCount = c64.PeekRam(0xC6),
        memoryConfig = c64.GetMemoryConfigDebugInfo(),
        cpuPort = c64.Peek(0x0001),
        vicD011 = c64.Peek(0xD011), vicD015 = c64.Peek(0xD015),
        vicD016 = c64.Peek(0xD016), vicD018 = c64.Peek(0xD018),
        border = c64.Peek(0xD020), background = c64.Peek(0xD021),
        irqVector = c64.PeekRam(0x0314) | (c64.PeekRam(0x0315) << 8),
        sid = c64.GetSidDebugInfo()
    });
    var framebuffer = c64.FrameBuffer;
    using var stream = File.Create(Path.Combine(outputDirectory, label + ".ppm"));
    var header = Encoding.ASCII.GetBytes($"P6\n{framebuffer.Width} {framebuffer.Height}\n255\n");
    stream.Write(header);
    foreach (uint pixel in framebuffer.CompletedPixels)
    {
        stream.WriteByte((byte)(pixel >> 16));
        stream.WriteByte((byte)(pixel >> 8));
        stream.WriteByte((byte)pixel);
    }
}
