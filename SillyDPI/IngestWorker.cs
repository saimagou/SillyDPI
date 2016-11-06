using System;
using System.IO;
using System.Net;
using System.Linq;
using System.Text;
using System.Threading;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Text.RegularExpressions;
/*    Yep, just trolling you here.	*/

namespace SillyDPI
{
	class IngestWorker
	{
		public static string ReplaceWith = "hOsT"; // Breaks non-RFC-compliant DPI systems
		public static int BreakEvery = 2; // Should break non-reconstructing DPI systems?

		public TcpClient Client { get; private set; }
		public NetworkStream Stream { get; private set; }
		public IPAddress ClientIP { get; private set; }
		public ushort ClientPort { get; private set; }
		public string ClientId { get; private set; }

		byte[] HeaderIngestBuffer = new byte[10240];
		List<string> RqHeaders = new List<string>();
		public byte[] Request { get; private set; }

		int HostStart = 0;
		int HostLength = 0;
		StringBuilder ModifiedRequest;

		string Host = null;
		IPAddress HostIP = null;

		int ThroughPutBufferSize = 20480;
		long ThroughPutContentLength = 0;
		byte[] ThroughPutBuffer = null;

		List<string> RcvHeaders = new List<string>();
		long DownloadContentLength = 0;
		byte[] DownloadBuffer = new byte[20480];


		public IngestWorker(TcpClient client)
		{
			Client = client;
			Stream = Client.GetStream();
		}

		public void ProcessRequest()
		{
			IPEndPoint EndPoint = Client.Client.RemoteEndPoint as IPEndPoint;
			ClientIP = EndPoint.Address;
			ClientPort = (ushort)EndPoint.Port;
			ClientId = ClientIP.ToString() + ":" + ClientPort;

			if (Client.Connected)
			{
				var before = DateTime.Now;
				while (!Stream.DataAvailable)
				{
					Thread.Sleep(20);
					if ((DateTime.Now - before).Milliseconds > 1000)
					{
						Console.WriteLine("{0} - No data sent within 1s", ClientId);
						Client.Close();
						return;
					}
				}
			}
			else return;

			using (var ms = new MemoryStream())
			{
				bool prevN = false;
				int prevoff = 0;
				int os = 0;
				DateTime before = DateTime.Now;
				try
				{
					while (true)
					{
						if (Stream.DataAvailable)
						{
							if (os > HeaderIngestBuffer.Length)
							{
								E400();
								Console.WriteLine("Request header exceeded buffer size!");
								return;
							}
							before = DateTime.Now;
							HeaderIngestBuffer[os] = (byte)Stream.ReadByte();

							if (HeaderIngestBuffer[os] == 13)
							{
								os++;
								continue;
							}
							if (HeaderIngestBuffer[os] == 10)
							{
								if (prevN)
									break;
								prevN = true;
								RqHeaders.Add(Encoding.UTF8.GetString(HeaderIngestBuffer, prevoff, os - prevoff).Trim());
								prevoff = os;
							}
							else prevN = false;
							os++;
						}
						else
						{
							Thread.Sleep(20);
							if ((DateTime.Now - before).TotalMilliseconds > 500)
							{
								Console.WriteLine("{0} - No data sent for 500ms", ClientId);
								Client.Close();
								return;
							}
						}

						if ((DateTime.Now - before).TotalMilliseconds > 1000)
						{
							Console.WriteLine("{0} - HTTP Header not sent within 1s", ClientId);
							Client.Close();
							return;
						}
					}

					ms.Write(HeaderIngestBuffer, 0, os + 1);
				}
				catch// (Exception E)
				{
					Console.WriteLine("{0} - Exception while receiving request header", ClientId);
					return;
				}
				Request = ms.ToArray();
			}

			int TotalData = 0;
			ModifiedRequest = new StringBuilder(Request.Length);
			//Console.WriteLine(RqHeaders[0]);
			for (int i = 0; i < RqHeaders.Count; i++)
			{
				string s = RqHeaders[i];

				Match N = Regex.Match(s, @"^Content-Length:\s+(\d+)$", RegexOptions.IgnoreCase);
				if (N.Success)
					ThroughPutContentLength = long.Parse(N.Groups[1].Value);

				N = Regex.Match(s, @"^Connection:\s+[^\s]+$", RegexOptions.IgnoreCase);
				if (N.Success)
					RqHeaders[i] = "Connection: close";

				N = Regex.Match(s, @"^(Host):\s+([^\s]+)$", RegexOptions.IgnoreCase);
				if (N.Success)
				{
					Host = N.Groups[2].Value;
					RqHeaders[i] = string.Format("{0}: {1}", ReplaceWith, Host);
					HostStart = TotalData;
					HostLength = RqHeaders[i].Length;
				}

				TotalData += RqHeaders[i].Length + 2;
				ModifiedRequest.Append(RqHeaders[i]);
				ModifiedRequest.Append("\r\n");
			}
			ModifiedRequest.Append("\r\n");

			//Console.WriteLine("Header size after upgrade: " + ModifiedRequest.Length);

			if (Host == null)
			{
				E400();
				return;
			}

			if ((HostIP = DNSManager.GetIP(Host)) == null)
			{
				Console.WriteLine("Host {0} not found!", Host);
				E404();
				return;
			}

			Request = Encoding.UTF8.GetBytes(ModifiedRequest.ToString());

			//Console.WriteLine("Alleged host header: \"{0}\"", Encoding.UTF8.GetString(Request, HostStart, HostLength).Replace("\r", "\\r").Replace("\n", "\\n"));
			//Console.WriteLine("Byte at host start index: {0}", Encoding.UTF8.GetString(new byte[1] { Request[HostStart] }).Replace("\r", "\\r").Replace("\n", "\\n"));
			//Console.WriteLine("Byte at host end index: {0}", Encoding.UTF8.GetString(new byte[1] { Request[HostStart + HostLength - 1] }).Replace("\r", "\\r").Replace("\n", "\\n"));

			var Target = new TcpClient();
			try
			{
				Target.Connect(HostIP, 80);
			}
			catch
			{
				E503();
				return;
			}

			var TgStream = Target.GetStream();

			try
			{
				int TotalSent = HostStart;
				TgStream.Write(Request, 0, HostStart);
				bool SendNotWrite = false;
				while (TotalSent < HostStart + HostLength)
				{
					if (!SendNotWrite) TgStream.Write(Request, TotalSent, BreakEvery);
					else Target.Client.Send(Request, TotalSent, BreakEvery, SocketFlags.None);
					TotalSent += BreakEvery;
					SendNotWrite = !SendNotWrite;
				}
				TgStream.Write(Request, TotalSent, Request.Length - TotalSent);
				TotalSent += Request.Length - TotalSent;
				//Console.WriteLine("Send valid: {0}", TotalSent == Request.Length);

				if (ThroughPutContentLength != 0)
				{
					ThroughPutBuffer = new byte[ThroughPutBufferSize];
					long bytesWritten = 0;
					int bytesRead = 0;
					while (Client.Connected && Target.Connected && bytesWritten < ThroughPutContentLength)
					{
						bytesRead = Stream.Read(ThroughPutBuffer, 0, ThroughPutBuffer.Length);
						TgStream.Write(ThroughPutBuffer, 0, bytesRead);
						bytesWritten += bytesRead;
					}
				}
			}
			catch// (Exception E)
			{
				Console.WriteLine("{0} - Exception while attempting to send request", Host);
				Target.Close();
				E400();
				return;
			}

			if (Target.Connected)
			{
				var before = DateTime.Now;
				while (!TgStream.DataAvailable)
				{
					Thread.Sleep(20);
					if ((DateTime.Now - before).Milliseconds > 1000)
					{
						Console.WriteLine("{0} - No response sent in 1s", Host);
						E503();
						Target.Close();
						return;
					}
				}
			}
			else
			{
				Console.WriteLine("{0} - Target disconnected before answering response", Host);
				E503();
				Target.Close();
				return;
			}

			try
			{
				bool prevN = false;
				int prevoff = 0;
				int os = 0;
				DateTime before = DateTime.Now;
				while (true)
				{
					if (TgStream.DataAvailable)
					{
						if (os > DownloadBuffer.Length)
						{
							E503();
							Target.Close();
							Console.WriteLine("{0} - Request header exceeded buffer size!", Host);
							return;
						}
						before = DateTime.Now;
						DownloadBuffer[os] = (byte)TgStream.ReadByte();
						Stream.WriteByte(DownloadBuffer[os]);
						if (DownloadBuffer[os] == 13)
						{
							os++;
							continue;
						}
						if (DownloadBuffer[os] == 10)
						{
							if (prevN)
								break;
							prevN = true;
							RcvHeaders.Add(Encoding.UTF8.GetString(DownloadBuffer, prevoff, os - prevoff).Trim());
							prevoff = os;
						}
						else prevN = false;
						os++;
					}
					else
					{
						Thread.Sleep(20);
						if ((DateTime.Now - before).TotalMilliseconds > 500)
						{
							Console.WriteLine("{0} - Response end not sent within 500 ms", Host);
							Client.Close();
							Target.Close();
							return;
						}
					}
				}

				foreach (string s in RcvHeaders)
				{
					Match N = Regex.Match(s, @"^\n?Content-Length:\s+(\d+)\r?\n?$", RegexOptions.IgnoreCase);
					if (N.Success)
						DownloadContentLength = long.Parse(N.Groups[1].Value);
				}

				int bytesRead = 0;
				int bytesTotal = 0;
				if (DownloadContentLength == 0)
				{
					while (Client.Connected && Target.Connected && TgStream.DataAvailable)
					{
						Stream.Write(DownloadBuffer, 0, TgStream.Read(DownloadBuffer, 0, DownloadBuffer.Length));
						Thread.Sleep(50);
					}
				}
				else
				{
					while (Client.Connected && Target.Connected && bytesTotal < DownloadContentLength)
					{
						bytesRead = TgStream.Read(DownloadBuffer, 0, DownloadBuffer.Length);
						bytesTotal += bytesRead;
						Stream.Write(DownloadBuffer, 0, bytesRead);
					}
				}
			}
			catch// (Exception E)
			{
#if DEBUG
				Console.WriteLine("{0} >>> {1} - Exception while handling response", ClientId, Host);
#endif
			}

			Client.Close();
			Target.Close();
			return;
		}

		private void E404()
		{
			byte[] write = Encoding.UTF8.GetBytes("HTTP/1.0 404 Not Found\nConnection: close\r\n\r\nHost Not Found");
			Console.WriteLine(ClientId + " - 404 Not Found");
			Stream.Write(write, 0, write.Length);
			Client.Close();
		}

		private void E503()
		{
			byte[] write = Encoding.UTF8.GetBytes("HTTP/1.0 503 Service unavailable\nConnection: close\r\n\r\nHost down or bad connectivity");
			Console.WriteLine(ClientId + " - 503 Service unavailable");
			Stream.Write(write, 0, write.Length);
			Client.Close();
		}

		private void E400()
		{
			byte[] write = Encoding.UTF8.GetBytes("HTTP/1.0 400 Bad Request\nConnection: close\r\n\r\nBad Request");
			Console.WriteLine(ClientId + " - 400 Bad Request");
			Stream.Write(write, 0, write.Length);
			Client.Close();
		}
	}
}
