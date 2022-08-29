using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace WSS_TCP_redirector
{
   class Program
   {
      static void Main(string[] args)
      {
         Redirect();
         Console.ReadLine();
      }
      static async void Redirect()
      {
         var server_client = new Server(62198);
         await server_client.Run();
      }
   }

   class Server
   {
      TcpListener tcpListener;
      int LastID_given = 0;

      public readonly SortedList<int, User> users;
      public readonly SortedList<int, ServerConnection> serverConnections;

      public Server(int port)
      {
         tcpListener = new TcpListener(new IPEndPoint(IPAddress.Any, port));

         users = new SortedList<int, User>();
         serverConnections = new SortedList<int, ServerConnection>();
      }
      public async Task Run()
      {
         tcpListener.Start();

         Console.WriteLine($"Server started.");

         while (true)
         {
            TcpClient wsClient = await tcpListener.AcceptTcpClientAsync();

            var ID = ++LastID_given;

            User user = new User(ID, this, wsClient);
            users.Add(ID, user);
            Console.WriteLine($"(+) A user connected. (ID: {ID})");
            Task clientTask = Task.Run(() =>
            {
               user.Run();
            });

            await Task.Delay(100);

            TcpClient tcpClient = new TcpClient();
            tcpClient.Connect(new IPEndPoint(IPAddress.Parse("127.0.0.1"), 64198));
            tcpClient.SendTimeout = 99999;

            ServerConnection serverConnection = new ServerConnection(ID, this, tcpClient);
            serverConnections.Add(ID, serverConnection);
            Console.WriteLine($"(+) Connected to the server for the user. (ID: {ID})");
            Task crossClientTask = Task.Run(() =>
            {
               serverConnection.Run();
            });

            user.serverConnection = serverConnection;
            serverConnection.user = user;
         }
      }
   }

   class User
   {
      bool connectionEstablished;
      readonly int id;
      Server server;

      public TcpClient wsClient;
      public NetworkStream stream;
      public Queue<byte[]> packetToBeSent = new Queue<byte[]>();
      public ServerConnection serverConnection;
      public bool isConnected = true;

      public User(int id, Server server, TcpClient wsClient)
      {
         stream = wsClient.GetStream();

         this.id = id;
         this.server = server;
         this.wsClient = wsClient;
      }

      // Received from CLIENT sent to SERVER
      public async Task Run()
      {
         try
         {
            while (isConnected)
            {
               byte[] bytes;

               if (connectionEstablished)
               {
                  while (packetToBeSent.Count > 0 && isConnected)
                  {
                     bytes = packetToBeSent.Dequeue();
                     stream.Write(bytes, 0, bytes.Length);
                  }
               }

               while (stream.DataAvailable && isConnected)
               {
                  bytes = new byte[wsClient.Available];
                  stream.Read(bytes, 0, bytes.Length);

                  if (connectionEstablished)
                  {
                     ulong msglen = (ulong)bytes[1] & 0b01111111;
                     int offset = 2;

                     if (msglen == 126)
                     {
                        msglen = BitConverter.ToUInt16(new byte[] { bytes[3], bytes[2] }, 0);
                        offset = 4;
                     }
                     else if (msglen == 127)
                     {
                        msglen = BitConverter.ToUInt64(new byte[] { bytes[9], bytes[8], bytes[7], bytes[6], bytes[5], bytes[4], bytes[3], bytes[2] }, 0);
                        offset = 10;
                     }

                     if (msglen != 0)
                     {
                        byte[] decoded = new byte[msglen];
                        byte[] masks = new byte[4] { bytes[offset], bytes[offset + 1], bytes[offset + 2], bytes[offset + 3] };
                        offset += 4;

                        for (ulong i = 0; i < msglen; ++i)
                           decoded[i] = (byte)(bytes[(ulong)offset + i] ^ masks[i % 4]);

                        #region CONSOLE WRITE
                        //string text = "";
                        //string text = Encoding.UTF8.GetString(decoded);
                        //string buffer_string = "";
                        //for (ulong i = 0; i < msglen; i++) buffer_string += "|" + decoded[i].ToString();
                        //Console.WriteLine($"<- (C{id}) Received: " + text + $"\n{buffer_string}\n");
                        #endregion

                        serverConnection.packetToBeSent.Enqueue(decoded);
                     }
                     else
                        Console.WriteLine($"<- (C{id}) Received: 0 length data");
                  }
                  else
                  {
                     string s = Encoding.UTF8.GetString(bytes);
                     if (Regex.IsMatch(s, "^GET", RegexOptions.IgnoreCase))
                     {
                        string swk = Regex.Match(s, "Sec-WebSocket-Key: (.*)").Groups[1].Value.Trim();
                        string swka = swk + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
                        byte[] swkaSha1 = System.Security.Cryptography.SHA1.Create().ComputeHash(Encoding.UTF8.GetBytes(swka));
                        string swkaSha1Base64 = Convert.ToBase64String(swkaSha1);

                        byte[] response = Encoding.UTF8.GetBytes("HTTP/1.1 101 Switching Protocols\r\n" +
                                                                 "Connection: Upgrade\r\n" +
                                                                 "Upgrade: websocket\r\n" +
                                                                 "Sec-WebSocket-Accept: " + swkaSha1Base64 + "\r\n\r\n");

                        stream.Write(response, 0, response.Length);

                        connectionEstablished = true;
                        await Task.Delay(200);
                     }
                  }
               }
            }
         }
         catch (Exception e)
         {
            Console.WriteLine($"(E) (C{id}) " + e.Message);
         }
         finally
         {
            isConnected = false;
            wsClient.Close();
            server.users.Remove(id);

            serverConnection.isConnected = false;
            serverConnection.tcpClient.Close();
            server.serverConnections.Remove(id);

            Console.WriteLine($"(X) (C{id}) The client is disconnected.");
         }
      }
   }

   class ServerConnection
   {
      readonly int id;
      Server server;

      public TcpClient tcpClient;
      public NetworkStream stream;
      public Queue<byte[]> packetToBeSent = new Queue<byte[]>();
      public User user;
      public bool isConnected = true;

      public ServerConnection(int id, Server server, TcpClient tcpClient)
      {
         stream = tcpClient.GetStream();

         this.id = id;
         this.server = server;
         this.tcpClient = tcpClient;
      }

      // Received from SERVER sent to CLIENT
      public async Task Run()
      {
         try
         {
            while (isConnected)
            {
               byte[] bytes;

               while (packetToBeSent.Count > 0 && isConnected)
               {
                  bytes = packetToBeSent.Dequeue();
                  stream.Write(bytes, 0, bytes.Length);
               }

               while (stream.DataAvailable && isConnected)
               {
                  bytes = new byte[tcpClient.Available];
                  stream.Read(bytes, 0, bytes.Length);

                  #region CONSOLE WRITE
                  //string text = "";
                  //string text = Encoding.UTF8.GetString(bytes);
                  //string buffer_string = "";
                  //for (int i = 0; i < bytes.Length; i++) buffer_string += "|" + bytes[i].ToString();
                  //Console.WriteLine($"<- (CC{id}) Received: " + text + $"\n{buffer_string}\n");
                  #endregion

                  SendToUser(bytes);
               }
            }
         }
         catch (Exception e)
         {
            Console.WriteLine($"(E) (CC{id}) " + e.Message);
         }
         finally
         {
            isConnected = false;
            tcpClient.Close();
            server.serverConnections.Remove(id);

            user.isConnected = false;
            user.wsClient.Close();
            server.users.Remove(id);

            Console.WriteLine($"(X) (CC{id}) The cross client is disconnected.");
         }
      }

      void SendToUser(byte[] sendBytes)
      {
         byte[] responseBytes = null;
         int length = sendBytes.Length;
         int offset = 0;

         if (length <= 125)
         {
            responseBytes = new byte[2 + length];
            responseBytes[1] = (byte)length;
            offset = 2;
         }
         else if (125 < length && length < 65535)
         {
            responseBytes = new byte[4 + length];
            responseBytes[1] = 126;
            responseBytes[2] = (byte)(length >> 8);
            responseBytes[3] = (byte)length;
            offset = 4;
         }

         responseBytes[0] = 0b10000010;

         for (int i = 0; i < sendBytes.Length; i++)
            responseBytes[offset + i] = sendBytes[i];

         user.packetToBeSent.Enqueue(responseBytes);

         #region CONSOLE WRITE
         //string buffer_string = "";
         //for (int i = 0; i < sendBytes.Length; i++)
         //buffer_string += "|" + sendBytes[i].ToString();
         //string text = Encoding.UTF8.GetString(sendBytes);
         //Console.WriteLine($"-> (CC{id}) Sent: " + text + $"\n{buffer_string}\n");
         #endregion
      }
   }
}