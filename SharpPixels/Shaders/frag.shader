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
/*#version 330 core
out vec4 FragColor;

void main()
{
    //FragColor = vec4(1.0f, 0.5f, 0.4f, 1.0f);
	FragColor = vec4(0.0f, 1.0f, 0.0f, 1.0f);
}*/

#version 330

in vec2 texCoord;
out vec4 outputColor;

uniform sampler2D texture0;

void main()
{
	outputColor = texture(texture0, texCoord);
	//outputColor = vec4(0.0f, 1.0f, 0.0f, 1.0f);
}