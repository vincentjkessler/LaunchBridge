using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace DevMind.LaunchBridge.SmartClickHost
{
    internal static class Program
    {
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer();

        private static int Main(string[] args)
        {
            try
            {
                using (Stream input = Console.OpenStandardInput())
                using (Stream output = Console.OpenStandardOutput())
                {
                    IDictionary<string, object> message = ReadMessage(input);
                    if (message == null)
                    {
                        WriteMessage(output, Response(false, false, "No Smart Click request was received.", null));
                        return 1;
                    }

                    string action = GetString(message, "action");
                    if (string.Equals(action, "ping", StringComparison.OrdinalIgnoreCase))
                    {
                        WriteMessage(output, Response(true, false, "Smart Click native helper is connected.", ""));
                        return 0;
                    }

                    string path = GetString(message, "path");
                    if (!string.Equals(action, "openDownloadedBuild", StringComparison.OrdinalIgnoreCase))
                    {
                        WriteMessage(output, Response(false, false, "Unsupported Smart Click action.", null));
                        return 1;
                    }

                    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    {
                        WriteMessage(output, Response(false, false, "The completed download could not be found.", null));
                        return 1;
                    }

                    path = Path.GetFullPath(path);
                    if (!IsSupportedPackage(path))
                    {
                        WriteMessage(output, Response(false, true, "This download is not a LaunchBridge app package.", Path.GetFileName(path)));
                        return 0;
                    }

                    string hostFolder = AppDomain.CurrentDomain.BaseDirectory;
                    string launchBridgeExe = Path.Combine(hostFolder, "LaunchBridge.exe");
                    if (!File.Exists(launchBridgeExe))
                    {
                        WriteMessage(output, Response(false, false, "LaunchBridge.exe is missing from the Smart Click host folder.", Path.GetFileName(path)));
                        return 1;
                    }

                    ProcessStartInfo start = new ProcessStartInfo();
                    start.FileName = launchBridgeExe;
                    start.Arguments = Quote(path) + " --smart-click --source-site " + Quote(GetString(message, "sourceSite"));
                    start.WorkingDirectory = hostFolder;
                    start.UseShellExecute = false;
                    start.CreateNoWindow = true;
                    Process process = Process.Start(start);
                    if (process == null)
                    {
                        WriteMessage(output, Response(false, false, "Windows did not start LaunchBridge.", Path.GetFileName(path)));
                        return 1;
                    }

                    WriteMessage(output, Response(true, false, "Sent to LaunchBridge.", Path.GetFileName(path)));
                    return 0;
                }
            }
            catch (Exception ex)
            {
                try
                {
                    using (Stream output = Console.OpenStandardOutput())
                        WriteMessage(output, Response(false, false, ex.Message, null));
                }
                catch { }
                return 1;
            }
        }

        private static bool IsSupportedPackage(string path)
        {
            string extension = (Path.GetExtension(path) ?? "").ToLowerInvariant();
            if (extension == ".zip" || extension == ".devmind") return true;

            try
            {
                string configPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DevMind", "LaunchBridge", "config.json");
                if (!File.Exists(configPath)) return false;
                object rootObject = Serializer.DeserializeObject(File.ReadAllText(configPath, Encoding.UTF8));
                IDictionary<string, object> root = rootObject as IDictionary<string, object>;
                if (root == null || !root.ContainsKey("RegisteredExtensions")) return false;
                IEnumerable values = root["RegisteredExtensions"] as IEnumerable;
                if (values == null) return false;
                foreach (object value in values)
                {
                    if (string.Equals(Convert.ToString(value), extension, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            catch { }
            return false;
        }

        private static IDictionary<string, object> ReadMessage(Stream input)
        {
            byte[] lengthBytes = ReadExact(input, 4);
            if (lengthBytes == null) return null;
            int length = BitConverter.ToInt32(lengthBytes, 0);
            if (length < 2 || length > 4 * 1024 * 1024) throw new InvalidDataException("Smart Click message size is invalid.");
            byte[] data = ReadExact(input, length);
            if (data == null) throw new EndOfStreamException("Smart Click message ended early.");
            object parsed = Serializer.DeserializeObject(Encoding.UTF8.GetString(data));
            return parsed as IDictionary<string, object>;
        }

        private static byte[] ReadExact(Stream input, int count)
        {
            byte[] buffer = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = input.Read(buffer, offset, count - offset);
                if (read <= 0) return offset == 0 ? null : buffer;
                offset += read;
            }
            return buffer;
        }

        private static void WriteMessage(Stream output, IDictionary<string, object> value)
        {
            byte[] data = Encoding.UTF8.GetBytes(Serializer.Serialize(value));
            byte[] length = BitConverter.GetBytes(data.Length);
            output.Write(length, 0, length.Length);
            output.Write(data, 0, data.Length);
            output.Flush();
        }

        private static IDictionary<string, object> Response(bool ok, bool ignored, string message, string fileName)
        {
            Dictionary<string, object> response = new Dictionary<string, object>();
            response["ok"] = ok;
            response["ignored"] = ignored;
            response["message"] = message ?? "";
            response["fileName"] = fileName ?? "";
            return response;
        }

        private static string GetString(IDictionary<string, object> values, string key)
        {
            if (values == null || !values.ContainsKey(key) || values[key] == null) return "";
            return Convert.ToString(values[key]) ?? "";
        }

        private static string Quote(string value)
        {
            if (value == null) value = "";
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }
    }
}
