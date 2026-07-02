using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;

namespace DevMind.LaunchBridge
{
    public sealed class SingleInstanceCoordinator : IDisposable
    {
        private const string MutexName = "Local\\DevMind.LaunchBridge.SingleInstance.v1";
        private const string PipeName = "DevMind.LaunchBridge.CommandPipe.v1";
        private readonly Mutex mutex;
        private Thread listenerThread;
        private volatile bool stopping;

        public bool IsPrimary { get; private set; }

        public SingleInstanceCoordinator()
        {
            bool createdNew;
            mutex = new Mutex(true, MutexName, out createdNew);
            IsPrimary = createdNew;
        }

        public bool ForwardToPrimary(string[] args)
        {
            string message = EncodeArguments(args ?? new string[0]);
            Exception last = null;
            for (int attempt = 0; attempt < 12; attempt++)
            {
                try
                {
                    using (NamedPipeClientStream client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.Asynchronous))
                    {
                        client.Connect(90);
                        using (StreamWriter writer = new StreamWriter(client, new UTF8Encoding(false)))
                        {
                            writer.AutoFlush = true;
                            writer.WriteLine(message);
                        }
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    last = ex;
                    Thread.Sleep(20);
                }
            }
            try { System.Diagnostics.Debug.WriteLine("Could not forward request to the primary LaunchBridge instance: " + (last == null ? "unknown error" : last.Message)); }
            catch { }
            return false;
        }

        public void StartListening(Action<string[]> onCommand)
        {
            if (!IsPrimary) throw new InvalidOperationException("Only the primary instance can listen for commands.");
            if (onCommand == null) throw new ArgumentNullException("onCommand");
            if (listenerThread != null) return;

            listenerThread = new Thread(delegate()
            {
                while (!stopping)
                {
                    try
                    {
                        using (NamedPipeServerStream server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.None))
                        {
                            server.WaitForConnection();
                            if (stopping) return;
                            using (StreamReader reader = new StreamReader(server, Encoding.UTF8))
                            {
                                string line = reader.ReadLine();
                                string[] args = DecodeArguments(line);
                                try { onCommand(args); }
                                catch (Exception callbackError) { LaunchBridgeCore.Log("Single-instance callback error: " + callbackError); }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        if (!stopping)
                        {
                            LaunchBridgeCore.Log("Single-instance pipe error: " + ex.Message);
                            Thread.Sleep(250);
                        }
                    }
                }
            });
            listenerThread.IsBackground = true;
            listenerThread.Name = "LaunchBridge command pipe";
            listenerThread.Start();
        }

        private static string EncodeArguments(string[] args)
        {
            List<string> encoded = new List<string>();
            foreach (string arg in args)
            {
                string value = arg ?? "";
                encoded.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes(value)));
            }
            return string.Join("|", encoded.ToArray());
        }

        private static string[] DecodeArguments(string message)
        {
            if (string.IsNullOrEmpty(message)) return new string[0];
            string[] parts = message.Split(new char[] { '|' }, StringSplitOptions.None);
            List<string> args = new List<string>();
            foreach (string part in parts)
            {
                try { args.Add(Encoding.UTF8.GetString(Convert.FromBase64String(part))); }
                catch { args.Add(""); }
            }
            return args.ToArray();
        }

        public void Dispose()
        {
            stopping = true;
            try
            {
                using (NamedPipeClientStream client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out))
                {
                    client.Connect(100);
                    using (StreamWriter writer = new StreamWriter(client)) { writer.WriteLine(""); }
                }
            }
            catch { }
            try { if (mutex != null) mutex.Dispose(); } catch { }
        }
    }
}
