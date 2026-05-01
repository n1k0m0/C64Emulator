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
using OpenTK.Graphics.ES20;
using System;

namespace SharpPixels.Shaders
{
    /// <summary>
    /// Represents the shader component.
    /// </summary>
    public class Shader : IDisposable
    {        
        private string _vertexShaderSource;
        private string _fragmentShaderSource;
        private bool _disposedValue = false;

        public int Handle
        {
            private set;
            get;
        }

        /// <summary>
        /// Initializes a new Shader instance.
        /// </summary>
        public Shader(string vertexShaderSource, string fragmentShaderSource)
        {
            _vertexShaderSource = vertexShaderSource;
            _fragmentShaderSource = fragmentShaderSource;
        }

        /// <summary>
        /// Handles the compile operation.
        /// </summary>
        public bool Compile()
        {
            var vertexShader = GL.CreateShader(ShaderType.VertexShader);
            GL.ShaderSource(vertexShader, _vertexShaderSource);

            var fragmentShader = GL.CreateShader(ShaderType.FragmentShader);
            GL.ShaderSource(fragmentShader, _fragmentShaderSource);

            GL.CompileShader(vertexShader);

            string infoLogVert = GL.GetShaderInfoLog(vertexShader);
            if (infoLogVert != string.Empty)
            {
                Console.WriteLine(infoLogVert);
                return false;
            }

            GL.CompileShader(fragmentShader);

            string infoLogFrag = GL.GetShaderInfoLog(fragmentShader);

            if (infoLogFrag != string.Empty)
            {
                Console.WriteLine(infoLogFrag);
                return false;
            }

            Handle = GL.CreateProgram();

            GL.AttachShader(Handle, vertexShader);
            GL.AttachShader(Handle, fragmentShader);

            GL.LinkProgram(Handle);

            GL.DetachShader(Handle, vertexShader);
            GL.DetachShader(Handle, fragmentShader);
            GL.DeleteShader(fragmentShader);
            GL.DeleteShader(vertexShader);

            return true;
        }

        /// <summary>
        /// Releases resources owned by the component.
        /// </summary>
        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                GL.DeleteProgram(Handle);
                _disposedValue = true;
            }
        }

        /// <summary>
        /// Releases resources owned by the component.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Handles the use operation.
        /// </summary>
        public void Use()
        {
            GL.UseProgram(Handle);
        }
    }
}
