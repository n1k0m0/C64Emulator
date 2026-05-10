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
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;

namespace C64Emulator.Core
{
    /// <summary>
    /// Writes golden run results in a JUnit-compatible XML shape.
    /// </summary>
    public static class GoldenJUnitResultWriter
    {
        /// <summary>
        /// Converts a run result to JUnit XML.
        /// </summary>
        public static string ToXml(GoldenRunResult result)
        {
            if (result == null)
            {
                throw new ArgumentNullException("result");
            }

            var builder = new StringBuilder();
            var settings = new XmlWriterSettings();
            settings.Indent = true;
            settings.OmitXmlDeclaration = false;

            using (XmlWriter writer = XmlWriter.Create(builder, settings))
            {
                WriteDocument(writer, result);
            }

            return builder.ToString();
        }

        /// <summary>
        /// Writes a run result to a JUnit XML file.
        /// </summary>
        public static void Write(string path, GoldenRunResult result)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("Output path must not be empty.", "path");
            }

            string directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, ToXml(result));
        }

        private static void WriteDocument(XmlWriter writer, GoldenRunResult result)
        {
            writer.WriteStartDocument();
            writer.WriteStartElement("testsuites");
            writer.WriteAttributeString("tests", result.TestCount.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("failures", result.FailureCount.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("errors", result.ErrorCount.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("skipped", result.SkippedCount.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("time", Seconds(result.DurationMilliseconds));

            writer.WriteStartElement("testsuite");
            writer.WriteAttributeString("name", string.IsNullOrWhiteSpace(result.Name) ? "golden" : result.Name);
            writer.WriteAttributeString("tests", result.TestCount.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("failures", result.FailureCount.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("errors", result.ErrorCount.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("skipped", result.SkippedCount.ToString(CultureInfo.InvariantCulture));
            writer.WriteAttributeString("time", Seconds(result.DurationMilliseconds));

            if (!string.IsNullOrWhiteSpace(result.ManifestPath))
            {
                writer.WriteStartElement("properties");
                WriteProperty(writer, "manifestPath", result.ManifestPath);
                writer.WriteEndElement();
            }

            for (int index = 0; index < result.Results.Count; index++)
            {
                WriteTestCase(writer, result.Results[index]);
            }

            writer.WriteEndElement();
            writer.WriteEndElement();
            writer.WriteEndDocument();
        }

        private static void WriteTestCase(XmlWriter writer, GoldenTestResult result)
        {
            writer.WriteStartElement("testcase");
            writer.WriteAttributeString("classname", string.IsNullOrWhiteSpace(result.Category) ? "golden" : result.Category);
            writer.WriteAttributeString("name", string.IsNullOrWhiteSpace(result.Name) ? result.Id : result.Name);
            writer.WriteAttributeString("time", Seconds(result.DurationMilliseconds));

            if (result.Outcome == GoldenTestOutcome.Failed)
            {
                writer.WriteStartElement("failure");
                writer.WriteAttributeString("message", result.Message);
                writer.WriteString(result.Message);
                writer.WriteEndElement();
            }
            else if (result.Outcome == GoldenTestOutcome.Error)
            {
                writer.WriteStartElement("error");
                writer.WriteAttributeString("message", result.Message);
                writer.WriteString(result.Message);
                writer.WriteEndElement();
            }
            else if (result.Outcome == GoldenTestOutcome.Skipped)
            {
                writer.WriteStartElement("skipped");
                writer.WriteAttributeString("message", result.Message);
                writer.WriteEndElement();
            }

            if (HasProperties(result))
            {
                writer.WriteStartElement("properties");
                WriteProperties(writer, "expected.hash.", result.ExpectedHashes);
                WriteProperties(writer, "actual.hash.", result.ActualHashes);
                WriteProperties(writer, "expected.", result.ExpectedProperties);
                WriteProperties(writer, "actual.", result.ActualProperties);
                WriteProperties(writer, "artifact.", result.Artifacts);
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
        }

        private static bool HasProperties(GoldenTestResult result)
        {
            return HasAny(result.ExpectedHashes) ||
                   HasAny(result.ActualHashes) ||
                   HasAny(result.ExpectedProperties) ||
                   HasAny(result.ActualProperties) ||
                   HasAny(result.Artifacts);
        }

        private static bool HasAny(System.Collections.Generic.Dictionary<string, string> values)
        {
            return values != null && values.Count > 0;
        }

        private static void WriteProperties(XmlWriter writer, string prefix, System.Collections.Generic.Dictionary<string, string> values)
        {
            if (values == null)
            {
                return;
            }

            foreach (System.Collections.Generic.KeyValuePair<string, string> pair in values)
            {
                WriteProperty(writer, prefix + pair.Key, pair.Value);
            }
        }

        private static void WriteProperty(XmlWriter writer, string name, string value)
        {
            writer.WriteStartElement("property");
            writer.WriteAttributeString("name", name);
            writer.WriteAttributeString("value", value);
            writer.WriteEndElement();
        }

        private static string Seconds(long milliseconds)
        {
            double seconds = milliseconds / 1000.0;
            return seconds.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}
