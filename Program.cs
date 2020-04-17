using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace TraktorRegisty
{
    class Server
    {
        private static ConcurrentBag<ClientHandler> clients = new ConcurrentBag<ClientHandler>();
        //private static string ip = "127.0.0.1";
        private static int port = 8080;
        private static bool running = true;
        public static void Main()
        {
            if (Environment.OSVersion.Platform == PlatformID.Unix)
            {
                Console.WriteLine("This Registry runs on Unix with Enviromental Settings");
                Server.port = int.Parse(Environment.GetEnvironmentVariable("REGISTRY_PORT"));
            }
            else
            {
                Console.WriteLine("This Registry runs on NON-Unix with Default Settings");
                Server.port = 8080;
            }
            var server = new TcpListener(System.Net.IPAddress.Any, port);

            server.Start();
            Console.WriteLine("Server has started on {0}:{1}, Waiting for a connection...", System.Net.IPAddress.Any, port);


            // enter to an infinite cycle to be able to handle every change in stream
            while (running)
            {
                TcpClient client = server.AcceptTcpClient();
                Console.WriteLine("A client connected.");
                ClientHandler clientHandler = new ClientHandler(client);
                clients.Add(clientHandler);
            }
        }
        public static void Broadcast(string message)
        {
            foreach (var client in clients)
            {
                client.sendMessage(message);
            }
        }
        public static void DistributeMessage(string message, ClientHandler self)
        {
            foreach (var client in clients)
            {
                if (!client.Equals(self))
                {
                    client.sendMessage(message);
                }
            }
        }
        public static void RemoveClient(ClientHandler client)
        {
            clients.TryTake(out client);
        }
    }
    internal class ClientHandler
    {
        TcpClient client;
        NetworkStream stream;
        public ClientHandler(TcpClient client)
        {
            this.client = client;
            stream = client.GetStream();
            Thread ctThread = new Thread(Run);
            ctThread.Start();
        }
        public void Run()
        {
            while (true)
            {

                while (!stream.DataAvailable) ;
                while (client.Available < 3) ; // match against "get"

                byte[] bytes = new byte[client.Available];
                stream.Read(bytes, 0, client.Available);
                string s = Encoding.UTF8.GetString(bytes);


                if (Regex.IsMatch(s, "^GET", RegexOptions.IgnoreCase))
                {
                    HandShake(s);
                }
                else
                {
                    bool fin = (bytes[0] & 0b10000000) != 0,
                        mask = (bytes[1] & 0b10000000) != 0; // must be true, "All messages from the client to the server have this bit set"

                    int opcode = bytes[0] & 0b00001111, // expecting 1 - text message
                        msglen = bytes[1] - 128, // & 0111 1111
                        offset = 2;
                    if (opcode == 1)
                    {
                        Console.WriteLine("Recieving Text message from Client");

                    }
                    else if (opcode == 8)
                    {
                        Console.WriteLine("Closing Websocket");
                        RespondeTocloseWebsocket();
                        byte[] decoded = new byte[msglen];
                        byte[] masks = new byte[4] { bytes[offset], bytes[offset + 1], bytes[offset + 2], bytes[offset + 3] };
                        offset += 4;

                        for (int i = 0; i < msglen; ++i) //2 Offset
                            decoded[i] = (byte)(bytes[offset + i] ^ masks[i % 4]);

                        string text = Encoding.UTF8.GetString(decoded);
                        Console.WriteLine("Client-Closing-Message: {0}", text);
                        break;
                    }
                    if (msglen == 126)
                    {
                        // was ToUInt16(bytes, offset) but the result is incorrect
                        msglen = BitConverter.ToUInt16(new byte[] { bytes[3], bytes[2] }, 0);
                        offset = 4;
                    }
                    else if (msglen == 127)
                    {
                        Console.WriteLine("TODO: msglen == 127, needs qword to store msglen");
                        // i don't really know the byte order, please edit this
                        // msglen = BitConverter.ToUInt64(new byte[] { bytes[5], bytes[4], bytes[3], bytes[2], bytes[9], bytes[8], bytes[7], bytes[6] }, 0);
                        // offset = 10;
                    }
                    if (msglen == 0) Console.WriteLine("msglen == 0");
                    else if (mask)
                    {
                        byte[] decoded = new byte[msglen];
                        byte[] masks = new byte[4] { bytes[offset], bytes[offset + 1], bytes[offset + 2], bytes[offset + 3] };
                        offset += 4;

                        for (int i = 0; i < msglen; ++i)
                            decoded[i] = (byte)(bytes[offset + i] ^ masks[i % 4]);

                        string text = Encoding.UTF8.GetString(decoded);
                        Console.WriteLine("Client-Message: {0}", text);
                        Server.DistributeMessage(text, this);
                    }
                    else Console.WriteLine("mask bit not set");
                }
            }
        }
        public void sendMessage(byte[] message)
        {
            stream.Write(message, 0, message.Length);
        }
        public void sendMessage(string message)
        {
            byte[] codedMessage = Encoding.UTF8.GetBytes(message);
            sendMessage(BuildMessage(codedMessage));
        }
        public byte[] BuildMessage(byte[] message)
        {
            /* fin + rsv1 + rsv2 + rsv3 + opcode = 1 Byte
             * mask + payload_len = 1 byte
             * payload = message.length
             */
            byte[] build = new byte[2 + message.Length];
            byte[] payload_len = BitConverter.GetBytes(message.Length);
            build[0] = 0b10000001;
            build[1] = payload_len.Length>1?payload_len[0]:throw new ArgumentException("playload length to big"); //if Message exceeds firstpayload -> unlucky
            for (int i = 0; i < build[1] ; ++i)
            {
                build[i + 2] = message[i];
            }
            return build;
        }
        public void RespondeTocloseWebsocket()
        {
            byte[] closeFrame = new byte[2];
        //    closeFrame[0] = 0b1000; // [Fin|RSV1|RSV2|RSV3]
        //    closeFrame[1] = 0b1000; // [OPCode|OPCode|OPCode|OPCode]
        //    closeFrame[2] = 0b0000; // [Mask|Payload|Payload|Payload]
        //    closeFrame[3] = 0b0000; // [Payload|Payload|Payload|Payload]
            closeFrame[0] = 0b10001000;
            closeFrame[1] = 0b00000000;
            stream.Write(closeFrame);
            stream.Close();
            client.Close();
            Server.RemoveClient(this);
        }
        public void HandShake(string s)
        {
            Console.WriteLine("=====Handshaking from client=====\n{0}", s);

            // 1. Obtain the value of the "Sec-WebSocket-Key" request header without any leading or trailing whitespace
            // 2. Concatenate it with "258EAFA5-E914-47DA-95CA-C5AB0DC85B11" (a special GUID specified by RFC 6455)
            // 3. Compute SHA-1 and Base64 hash of the new value
            // 4. Write the hash back as the value of "Sec-WebSocket-Accept" response header in an HTTP response
            string swk = Regex.Match(s, "Sec-WebSocket-Key: (.*)").Groups[1].Value.Trim();
            string swka = swk + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
            byte[] swkaSha1 = System.Security.Cryptography.SHA1.Create().ComputeHash(Encoding.UTF8.GetBytes(swka));
            string swkaSha1Base64 = Convert.ToBase64String(swkaSha1);

            // HTTP/1.1 defines the sequence CR LF as the end-of-line marker
            byte[] response = Encoding.UTF8.GetBytes(
                "HTTP/1.1 101 Switching Protocols\r\n" +
                "Connection: Upgrade\r\n" +
                "Upgrade: websocket\r\n" +
                "Sec-WebSocket-Accept: " + swkaSha1Base64 + "\r\n\r\n");

            stream.Write(response, 0, response.Length);
        }
    }
}

