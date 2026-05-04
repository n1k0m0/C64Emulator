/*
   Copyright 2026 Nils Kopal <Nils.Kopal<at>kopaldev.de

   Licensed under the Apache License, Version 2.0 (the "License");
   you may not use this file except in compliance with the License.
   You may obtain a copy of the License at

       http://www.apache.org/licenses/LICENSE-2.0

   Unless required by applicable law or agreed to in writing, software
   distributed under the License is distributed on an "AS IS" BASIS,
   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
   See the License for the specific language governing permissions and
   limitations under the License.
*/
using System;
using System.Collections.Generic;
using System.IO;

namespace C64Emulator.Core
{
    /// <summary>
    /// Lists the supported iec bus line values.
    /// </summary>
    public enum IecBusLine
    {
        Atn,
        Clock,
        Data,
        ServiceRequest
    }

    /// <summary>
    /// Represents the iec bus component.
    /// </summary>
    public sealed class IecBus
    {
        private readonly List<Action<IecBusLine, bool>> _lineChangeListeners =
            new List<Action<IecBusLine, bool>>();
        private Action<IecBusLine, bool>[] _lineChangeListenerSnapshot =
            new Action<IecBusLine, bool>[0];

        /// <summary>
        /// Represents the participant state component.
        /// </summary>
        private sealed class ParticipantState
        {
            public bool AtnLow;
            public bool ClockLow;
            public bool DataLow;
            public bool ServiceRequestLow;
        }

        private readonly Dictionary<string, ParticipantState> _participants =
            new Dictionary<string, ParticipantState>(StringComparer.OrdinalIgnoreCase);
        private int _atnLowCount;
        private int _clockLowCount;
        private int _dataLowCount;
        private int _serviceRequestLowCount;

        /// <summary>
        /// Creates port.
        /// </summary>
        public IecBusPort CreatePort(string ownerName)
        {
            if (string.IsNullOrWhiteSpace(ownerName))
            {
                throw new ArgumentException("Owner name is required.", nameof(ownerName));
            }

            if (_participants.ContainsKey(ownerName))
            {
                throw new InvalidOperationException("An IEC bus participant with the same name already exists.");
            }

            _participants.Add(ownerName, new ParticipantState());
            return new IecBusPort(this, ownerName);
        }

        /// <summary>
        /// Returns whether line low is true.
        /// </summary>
        public bool IsLineLow(IecBusLine line)
        {
            return GetAggregateLineState(line);
        }

        /// <summary>
        /// Returns whether line high is true.
        /// </summary>
        public bool IsLineHigh(IecBusLine line)
        {
            return !IsLineLow(line);
        }

        /// <summary>
        /// Handles the register line change listener operation.
        /// </summary>
        public void RegisterLineChangeListener(Action<IecBusLine, bool> listener)
        {
            if (listener == null)
            {
                throw new ArgumentNullException(nameof(listener));
            }

            _lineChangeListeners.Add(listener);
            _lineChangeListenerSnapshot = _lineChangeListeners.ToArray();
        }

        /// <summary>
        /// Gets the line owners debug value.
        /// </summary>
        public string GetLineOwnersDebug(IecBusLine line)
        {
            var owners = new List<string>();
            foreach (KeyValuePair<string, ParticipantState> entry in _participants)
            {
                bool low = false;
                switch (line)
                {
                    case IecBusLine.Atn:
                        low = entry.Value.AtnLow;
                        break;
                    case IecBusLine.Clock:
                        low = entry.Value.ClockLow;
                        break;
                    case IecBusLine.Data:
                        low = entry.Value.DataLow;
                        break;
                    case IecBusLine.ServiceRequest:
                        low = entry.Value.ServiceRequestLow;
                        break;
                }

                if (low)
                {
                    owners.Add(entry.Key);
                }
            }

            return owners.Count == 0 ? "-" : string.Join(",", owners);
        }

        /// <summary>
        /// Writes all IEC participant line states into a savestate stream.
        /// </summary>
        public void SaveState(BinaryWriter writer)
        {
            writer.Write(_participants.Count);
            foreach (KeyValuePair<string, ParticipantState> entry in _participants)
            {
                writer.Write(entry.Key);
                writer.Write(entry.Value.AtnLow);
                writer.Write(entry.Value.ClockLow);
                writer.Write(entry.Value.DataLow);
                writer.Write(entry.Value.ServiceRequestLow);
            }
        }

        /// <summary>
        /// Restores all IEC participant line states from a savestate stream.
        /// </summary>
        public void LoadState(BinaryReader reader)
        {
            bool oldAtnLow = IsLineLow(IecBusLine.Atn);
            bool oldClockLow = IsLineLow(IecBusLine.Clock);
            bool oldDataLow = IsLineLow(IecBusLine.Data);
            bool oldServiceRequestLow = IsLineLow(IecBusLine.ServiceRequest);

            foreach (ParticipantState participant in _participants.Values)
            {
                participant.AtnLow = false;
                participant.ClockLow = false;
                participant.DataLow = false;
                participant.ServiceRequestLow = false;
            }

            int count = reader.ReadInt32();
            for (int index = 0; index < count; index++)
            {
                string owner = reader.ReadString();
                bool atnLow = reader.ReadBoolean();
                bool clockLow = reader.ReadBoolean();
                bool dataLow = reader.ReadBoolean();
                bool serviceRequestLow = reader.ReadBoolean();
                ParticipantState participant;
                if (_participants.TryGetValue(owner, out participant))
                {
                    participant.AtnLow = atnLow;
                    participant.ClockLow = clockLow;
                    participant.DataLow = dataLow;
                    participant.ServiceRequestLow = serviceRequestLow;
                }
            }

            RecomputeAggregateCounts();
            NotifyIfRestoredLineChanged(IecBusLine.Atn, oldAtnLow);
            NotifyIfRestoredLineChanged(IecBusLine.Clock, oldClockLow);
            NotifyIfRestoredLineChanged(IecBusLine.Data, oldDataLow);
            NotifyIfRestoredLineChanged(IecBusLine.ServiceRequest, oldServiceRequestLow);
        }

        /// <summary>
        /// Returns whether owner driving line low is true.
        /// </summary>
        internal bool IsOwnerDrivingLineLow(string ownerName, IecBusLine line)
        {
            ParticipantState participant;
            if (!_participants.TryGetValue(ownerName, out participant))
            {
                return false;
            }

            switch (line)
            {
                case IecBusLine.Atn:
                    return participant.AtnLow;
                case IecBusLine.Clock:
                    return participant.ClockLow;
                case IecBusLine.Data:
                    return participant.DataLow;
                case IecBusLine.ServiceRequest:
                    return participant.ServiceRequestLow;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Sets the line state value.
        /// </summary>
        internal void SetLineState(string ownerName, IecBusLine line, bool driveLow)
        {
            ParticipantState participant;
            if (!_participants.TryGetValue(ownerName, out participant))
            {
                throw new InvalidOperationException("Unknown IEC bus participant.");
            }

            bool wasLow = GetAggregateLineState(line);
            bool changed = ApplyParticipantLineState(participant, line, driveLow);
            bool isLow = GetAggregateLineState(line);
            if (changed && wasLow != isLow)
            {
                NotifyLineChanged(line, isLow);
            }
        }

        /// <summary>
        /// Sets the line states value.
        /// </summary>
        internal void SetLineStates(
            string ownerName,
            bool? atnLow = null,
            bool? clockLow = null,
            bool? dataLow = null,
            bool? serviceRequestLow = null)
        {
            ParticipantState participant;
            if (!_participants.TryGetValue(ownerName, out participant))
            {
                throw new InvalidOperationException("Unknown IEC bus participant.");
            }

            bool oldAtnLow = GetAggregateLineState(IecBusLine.Atn);
            bool oldClockLow = GetAggregateLineState(IecBusLine.Clock);
            bool oldDataLow = GetAggregateLineState(IecBusLine.Data);
            bool oldServiceRequestLow = GetAggregateLineState(IecBusLine.ServiceRequest);

            if (atnLow.HasValue)
            {
                ApplyParticipantLineState(participant, IecBusLine.Atn, atnLow.Value);
            }

            if (clockLow.HasValue)
            {
                ApplyParticipantLineState(participant, IecBusLine.Clock, clockLow.Value);
            }

            if (dataLow.HasValue)
            {
                ApplyParticipantLineState(participant, IecBusLine.Data, dataLow.Value);
            }

            if (serviceRequestLow.HasValue)
            {
                ApplyParticipantLineState(participant, IecBusLine.ServiceRequest, serviceRequestLow.Value);
            }

            NotifyIfChanged(IecBusLine.Atn, oldAtnLow);
            NotifyIfChanged(IecBusLine.Clock, oldClockLow);
            NotifyIfChanged(IecBusLine.Data, oldDataLow);
            NotifyIfChanged(IecBusLine.ServiceRequest, oldServiceRequestLow);
        }

        /// <summary>
        /// Handles the notify if changed operation.
        /// </summary>
        private void NotifyIfChanged(IecBusLine line, bool oldIsLow)
        {
            bool isLow = GetAggregateLineState(line);
            if (oldIsLow != isLow)
            {
                NotifyLineChanged(line, isLow);
            }
        }

        /// <summary>
        /// Notifies listeners after a bulk savestate restore if a line changed.
        /// </summary>
        private void NotifyIfRestoredLineChanged(IecBusLine line, bool oldIsLow)
        {
            bool isLow = GetAggregateLineState(line);
            if (oldIsLow != isLow)
            {
                NotifyLineChanged(line, isLow);
            }
        }

        /// <summary>
        /// Rebuilds cached wired-AND line counts from participant states.
        /// </summary>
        private void RecomputeAggregateCounts()
        {
            _atnLowCount = 0;
            _clockLowCount = 0;
            _dataLowCount = 0;
            _serviceRequestLowCount = 0;
            foreach (ParticipantState participant in _participants.Values)
            {
                if (participant.AtnLow)
                {
                    _atnLowCount++;
                }

                if (participant.ClockLow)
                {
                    _clockLowCount++;
                }

                if (participant.DataLow)
                {
                    _dataLowCount++;
                }

                if (participant.ServiceRequestLow)
                {
                    _serviceRequestLowCount++;
                }
            }
        }

        /// <summary>
        /// Gets the current wired-AND state of a bus line.
        /// </summary>
        private bool GetAggregateLineState(IecBusLine line)
        {
            switch (line)
            {
                case IecBusLine.Atn:
                    return _atnLowCount > 0;
                case IecBusLine.Clock:
                    return _clockLowCount > 0;
                case IecBusLine.Data:
                    return _dataLowCount > 0;
                case IecBusLine.ServiceRequest:
                    return _serviceRequestLowCount > 0;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Updates one participant output and keeps the cached bus aggregate in sync.
        /// </summary>
        private bool ApplyParticipantLineState(ParticipantState participant, IecBusLine line, bool driveLow)
        {
            switch (line)
            {
                case IecBusLine.Atn:
                    if (participant.AtnLow == driveLow)
                    {
                        return false;
                    }

                    participant.AtnLow = driveLow;
                    _atnLowCount += driveLow ? 1 : -1;
                    return true;
                case IecBusLine.Clock:
                    if (participant.ClockLow == driveLow)
                    {
                        return false;
                    }

                    participant.ClockLow = driveLow;
                    _clockLowCount += driveLow ? 1 : -1;
                    return true;
                case IecBusLine.Data:
                    if (participant.DataLow == driveLow)
                    {
                        return false;
                    }

                    participant.DataLow = driveLow;
                    _dataLowCount += driveLow ? 1 : -1;
                    return true;
                case IecBusLine.ServiceRequest:
                    if (participant.ServiceRequestLow == driveLow)
                    {
                        return false;
                    }

                    participant.ServiceRequestLow = driveLow;
                    _serviceRequestLowCount += driveLow ? 1 : -1;
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Handles the notify line changed operation.
        /// </summary>
        private void NotifyLineChanged(IecBusLine line, bool isLow)
        {
            Action<IecBusLine, bool>[] listeners = _lineChangeListenerSnapshot;
            if (listeners.Length == 0)
            {
                return;
            }

            for (int index = 0; index < listeners.Length; index++)
            {
                listeners[index](line, isLow);
            }
        }
    }

    /// <summary>
    /// Represents the iec bus port component.
    /// </summary>
    public sealed class IecBusPort
    {
        private readonly IecBus _bus;
        private readonly string _ownerName;

        /// <summary>
        /// Initializes a new IecBusPort instance.
        /// </summary>
        internal IecBusPort(IecBus bus, string ownerName)
        {
            _bus = bus;
            _ownerName = ownerName;
        }

        /// <summary>
        /// Sets the line low value.
        /// </summary>
        public void SetLineLow(IecBusLine line, bool driveLow)
        {
            _bus.SetLineState(_ownerName, line, driveLow);
        }

        /// <summary>
        /// Returns whether line low is true.
        /// </summary>
        public bool IsLineLow(IecBusLine line)
        {
            return _bus.IsLineLow(line);
        }

        /// <summary>
        /// Returns whether line high is true.
        /// </summary>
        public bool IsLineHigh(IecBusLine line)
        {
            return _bus.IsLineHigh(line);
        }

        /// <summary>
        /// Handles the register line change listener operation.
        /// </summary>
        public void RegisterLineChangeListener(Action<IecBusLine, bool> listener)
        {
            _bus.RegisterLineChangeListener(listener);
        }

        /// <summary>
        /// Returns whether owner driving line low is true.
        /// </summary>
        public bool IsOwnerDrivingLineLow(string ownerName, IecBusLine line)
        {
            return _bus.IsOwnerDrivingLineLow(ownerName, line);
        }

        /// <summary>
        /// Sets the lines value.
        /// </summary>
        public void SetLines(
            bool? atnLow = null,
            bool? clockLow = null,
            bool? dataLow = null,
            bool? serviceRequestLow = null)
        {
            _bus.SetLineStates(_ownerName, atnLow, clockLow, dataLow, serviceRequestLow);
        }
    }
}
