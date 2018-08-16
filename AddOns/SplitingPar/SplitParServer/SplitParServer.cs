using CommonLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SplitParServer
{
    public class SplitParServer
    {
        public static Dictionary<string, Utils.CurrentState> ClientStates = new Dictionary<string, Utils.CurrentState>();
        public static List<BplTask> BplTasks = new List<BplTask>();
        static SplitParConfig config;
        static List<Socket> clients = new List<Socket>(); 
        
        static int maxClients = 1;  

        public enum Outcome
        {
            Correct,
            Errors,
            TimedOut,
            OutOfResource,
            OutOfMemory,
            Inconclusive,
            ReachedBound
        } 
        
        static void TransferFiles(string folderDir, string folder, string remoteFolder)
        {
            LogWithAddress.WriteLine(string.Format("Copying folder {0} to {1}", folder, remoteFolder));
            var files = System.IO.Directory.GetFiles(System.IO.Path.Combine(folderDir, folder));
            foreach (var file in files)
            {
                var remotefile = System.IO.Path.Combine(remoteFolder, folder, System.IO.Path.GetFileName(file));
                if (!System.IO.File.Exists(remotefile))
                {
                    System.IO.Directory.CreateDirectory(System.IO.Path.Combine(remoteFolder, folder)); 
                    System.IO.File.Copy(file, remotefile, true);
                }
            }
        }

        static bool PingClient(int clientID)
        { 
            clients.ElementAt(clientID).Send(Utils.EncodeStr(Utils.PingMsg));
        
            // wait for the reply message
            byte[] data = new byte[1024];
            int receivedDataLength = clients.ElementAt(clientID).Receive(data); //Wait for the data
            string stringData = Encoding.ASCII.GetString(data, 0, receivedDataLength); //Decode the data received
            if (stringData.Equals(Utils.ReadyMsg))
                return true;
            else
                return false;
        }

        static Outcome ListenToCompletion(int clientID)
        {
            byte[] data = new byte[1024];
            int receivedDataLength = clients.ElementAt(clientID).Receive(data); //Wait for the data
            string stringData = Encoding.ASCII.GetString(data, 0, receivedDataLength); //Decode the data received
            if (stringData.Contains(Utils.CorrectMsg))
                return Outcome.Correct;
            else if (stringData.Contains(Utils.ErrorMsg))
                return Outcome.Errors;
            else if (stringData.Contains(Utils.ReachedBoundMsg))
                return Outcome.ReachedBound;
            else if (stringData.Contains(Utils.TimedoutMsg))
                return Outcome.TimedOut;
            else if (stringData.Contains(Utils.OutOfMemoryMsg))
                return Outcome.OutOfMemory;
            else 
                return Outcome.OutOfResource;
        }

        static void CloseConnection()
        {
            LogWithAddress.WriteLine(string.Format(Utils.CompletionMsg)); 
            foreach (var client in clients)
            {
                if (client != null)
                {
                    client.Send(Utils.EncodeStr(Utils.CompletionMsg));
                    client.Close();
                }
            }
        }

        static void InstallingClients()
        { 
            var force = true;
            LogWithAddress.WriteLine(string.Format("Checking self installation"));
            try
            {
                Installer.CheckSelfInstall(config);
            }
            catch (Exception e)
            {
                LogWithAddress.WriteLine(string.Format("{0}", e.Message));
                return;
            }
            LogWithAddress.WriteLine(string.Format("Done"));
            ClientStates[config.root] = Utils.CurrentState.AVAIL;

            // Do remote installation
            LogWithAddress.WriteLine(string.Format("Doing remote installation")); 
            for (int i = 0; i < config.RemoteRoots.Count; ++i)
            {
                // local machine
                ClientStates[config.RemoteRoots[i].value] = Utils.CurrentState.AVAIL;
                LogWithAddress.WriteLine(string.Format("Installing {0}", config.RemoteRoots[i].value));
                Installer.RemoteInstall(config.root, config.RemoteRoots[i].value, config.Utils.Select(u => u.dir).Distinct(), force, config.BoogieFiles); 
            }
            LogWithAddress.WriteLine(string.Format("Done"));
        }

        static void RunClients()
        {
            var threads = new List<Thread>();
            var workers = new List<Worker>();

            var starttime = DateTime.Now;
            Console.WriteLine("Spawning clients");

            // spawn client on own machine 
            config.DumpClientConfig(config.root, System.IO.Path.Combine(config.root, Utils.ClientConfig), config.BoogieFiles);
            var configDir = System.IO.Path.Combine(config.root, Utils.ClientConfig);
            var w0 = new Worker(config.root, false, configDir);
            workers.Add(w0);
            threads.Add(new Thread(new ThreadStart(w0.Run)));
            

            // spawn client on remote machines
            for (int i = 0; i < config.RemoteRoots.Count; ++i)
            { 
                string clientRoot = config.RemoteRoots[i].value;
                configDir = System.IO.Path.Combine(clientRoot, Utils.ClientConfig);
                config.DumpClientConfig(clientRoot, configDir, config.BoogieFiles);                
                var w1 = new Worker(clientRoot, true, configDir);
                threads.Add(new Thread(new ThreadStart(w1.Run)));
                workers.Add(w1);
            }
            // start threads
            threads.ForEach(t => t.Start());
        }

        static void ConnectClient(string hostName)
        {
            try
            {
                IPHostEntry ipHostInfo = Dns.Resolve(hostName);
                IPAddress ipAddress = ipHostInfo.AddressList[0];
                IPEndPoint remoteEP = new IPEndPoint(ipAddress, Utils.ServerPort); 

                Socket listener = new Socket(AddressFamily.InterNetwork,
                    SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    listener.Connect(remoteEP);

                    LogWithAddress.WriteLine(string.Format("Socket connected {0}", listener.RemoteEndPoint.ToString()));

                    byte[] data = new byte[Utils.MsgSize];
                    int receivedDataLength = listener.Receive(data); //Wait for the data
                    string stringData = Encoding.ASCII.GetString(data, 0, receivedDataLength); //Decode the data received
                    LogWithAddress.WriteLine(string.Format("{0}", stringData)); //Write the data on the screen

                    // reply the client
                    listener.Send(Utils.EncodeStr("Hi " + listener.RemoteEndPoint.ToString()));
                    clients.Add(listener);
                }
                catch (ArgumentNullException ane)
                {
                    LogWithAddress.WriteLine(string.Format("ArgumentNullException : {0}", ane.ToString()));
                }
                catch (SocketException se)
                {
                    LogWithAddress.WriteLine(string.Format("SocketException : {0}", se.ToString()));
                }
                catch (Exception e)
                {
                    LogWithAddress.WriteLine(string.Format("Unexpected exception : {0}", e.ToString()));
                }

            }
            catch
            {
                LogWithAddress.WriteLine(string.Format("Cannot connect to Server"));
            }

        }

        static void CreateConnection()
        {
            lock (LogWithAddress.debugOut)
            {
                LogWithAddress.WriteLine(string.Format("Set up connections"));
            }
            IPHostEntry ipHostInfo = Dns.Resolve(Dns.GetHostName());
            IPAddress ipAddress = ipHostInfo.AddressList[0];
            IPEndPoint localEndPoint = new IPEndPoint(ipAddress, Utils.ServerPort);


            Socket listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
             
            try
            {
                listener.Bind(localEndPoint);
                listener.Listen(10);

                while (clients.Count < maxClients)
                {
                    lock (LogWithAddress.debugOut)
                    {
                        LogWithAddress.WriteLine(string.Format("Waiting for a connection..."));
                    }
                    clients.Add(listener.Accept());
                    lock (LogWithAddress.debugOut)
                    {
                        LogWithAddress.WriteLine(string.Format("Connected"));
                    }
                    clients.ElementAt(clients.Count - 1).Send(Utils.EncodeStr("Hello " + clients.ElementAt(clients.Count - 1).RemoteEndPoint.ToString()));

                    // wait for the reply message
                    byte[] data = new byte[Utils.MsgSize];
                    int receivedDataLength = clients.ElementAt(clients.Count - 1).Receive(data); //Wait for the data
                    string stringData = Encoding.ASCII.GetString(data, 0, receivedDataLength); //Decode the data received

                    lock (LogWithAddress.debugOut)
                    {
                        LogWithAddress.WriteLine(string.Format("{0}", stringData)); //Write the data on the screen
                    }
                }
            }
            catch
            {
                lock (LogWithAddress.debugOut)
                {
                    LogWithAddress.WriteLine(string.Format("Error"));
                }
            }
        }

        static void TaskDelivery()
        {
            while (true)
            {
                int taskCount = 0;
                lock (BplTasks)
                {
                    taskCount = BplTasks.Count;
                } 
                if (taskCount > 0)
                {
                    Dictionary<string, Utils.CurrentState> localClientStates;
                    lock (ClientStates)
                    {                        
                        localClientStates = new Dictionary<string, Utils.CurrentState>(ClientStates);
                    }
                    foreach(var client in localClientStates)
                        if (client.Value == Utils.CurrentState.AVAIL)
                        {
                            BplTask topTask = BplTasks[0];
                            // let him do the first task
                            lock (LogWithAddress.debugOut)
                            {
                                LogWithAddress.WriteLine(string.Format("Transfer {0} to {1}", topTask.ToString(), client.Key));
                            }

                            // pick the task from a client
                            // if localClient is the one
                            if (client.Key.Equals(config.root))
                            {
                                System.IO.File.Move(topTask.callTreeDir, System.IO.Path.Combine(config.root, Utils.RunDir));
                            }
                            else
                            {
                                // find remote client
                                System.IO.File.Move(topTask.callTreeDir, System.IO.Path.Combine(client.Key, Utils.RunDir));
                            }

                            // mark him busy
                            lock (ClientStates)
                            {
                                //ClientStates[client.Key] = Utils.CurrentState.BUSY;
                            }

                            // remove the task
                            lock (BplTasks)
                            {
                                BplTasks.RemoveAt(0);
                            }

                            // send the task to client
                        }
                }
                lock (ClientStates)
                {
                    if (!ClientStates.Any(client => client.Value == Utils.CurrentState.BUSY))
                        break;
                }
            }
        }

        static void MonitoringClients()
        {
            var threads = new List<Thread>();
            var workers = new List<ServerListener>();

            var starttime = DateTime.Now;
            lock (LogWithAddress.debugOut)
            {
                LogWithAddress.WriteLine(string.Format("Monitoring clients"));
            }

            for (int i = 0; i < clients.Count; ++i)
            {
                var listener = new ServerListener(clients[i], config.RemoteRoots[i].value);
                threads.Add(new Thread(new ThreadStart(listener.Listen)));
                workers.Add(listener); 
            }

            threads.Add(new Thread(new ThreadStart(TaskDelivery)));
            // start threads & join
            threads.ForEach(t => t.Start());  
            threads.ForEach(t => t.Join());

            Console.WriteLine("Time taken = {0} seconds", (DateTime.Now - starttime).TotalSeconds.ToString("F2"));
        }

        static void MonitoringCorral()
        {
            var sep = new char[1];
            sep[0] = ':'; 

            string msg = "";
            while (!msg.Equals(Utils.CompletionMsg))
            {
                byte[] data = new byte[Utils.MsgSize];
                int receivedDataLength = clients.ElementAt(0).Receive(data); //Wait for the data
                msg = Encoding.ASCII.GetString(data, 0, receivedDataLength); //Decode the data received
                if (msg.Equals(Utils.CompletionMsg))
                    break;
                var split = msg.Split(sep);
                if (split.Length > 1)
                {
                    LogWithAddress.WriteLine(string.Format(Utils.Indent(int.Parse(split[0])) + ">>> " + split[1])); //Write the data on the screen
                }
            }
            LogWithAddress.WriteLine(string.Format("{0}", msg));
        }

        static void ServerController()
        {            
            InstallingClients();
            RunClients();
            Thread.Sleep(2000);
            if (true)
            {
                string localIP = Utils.LocalIP();
                if (localIP != null)
                {
                    ConnectClient(localIP);
                    foreach (var client in config.RemoteRoots)
                    {
                        string tmp = Utils.GetRemoteMachineName(client.value);
                        //ConnectClient(tmp);
                        lock (LogWithAddress.debugOut)
                        {
                            LogWithAddress.WriteLine(string.Format("Machine name: {0}", tmp));
                        }
                    }
                }
                else
                    new Exception(string.Format("Cannot get local IP"));
            }
            else
            {
                CreateConnection();
            }

            // local client is working
            lock (ClientStates)
            {
                ClientStates[config.root] = Utils.CurrentState.BUSY;
            }
            MonitoringClients();

            // connections are closed in ServerListener
            //CloseConnection();
        }

        static void Console_CancelKeyPress(object sender, ConsoleCancelEventArgs e)
        {
            Console.WriteLine("Got Ctrl-C");
            LogWithAddress.Close();
            lock (Utils.SpawnedProcesses)
            {
                foreach (var p in Utils.SpawnedProcesses)
                    p.Kill();
                Utils.SpawnedProcesses.Clear();
            }
            System.Diagnostics.Process.GetCurrentProcess().Kill();
        }

        static void Main(string[] args)
        {
            Console.CancelKeyPress += Console_CancelKeyPress;
            
            Debug.Assert(args.Length > 0);
            config = Utils.LoadConfig(args[0]);
            LogWithAddress.init(System.IO.Path.Combine(config.root, Utils.RunDir));
            ServerController();
            LogWithAddress.Close(); 
        }
    }
}
