using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace SillyDPI
{
	class Program
	{
		static string IP = "127.0.0.10";
		static int Port = 6386;

		static void Main(string[] args)
		{
			/*
			 * By the time you've got here, you've probably realised that this is not a tool for printers.
			 * This tool is a kind of a dumb implementation of an HTTP-only proxy,
			 * which only listens on 127.0.0.10:6386 and modifies every Host: header it gets
			 * to possibly slip past some even dumber deep packet inspection systems.
			 * This tool actually worked for my particular ISP, but there is no guarantee.
			 */
			var listener = new TcpListener(IPAddress.Parse(IP), Port);
			listener.Start(1);
			Console.WriteLine("Listening {0}:{1}...", IP, Port);

			while (true)
			{
				TcpClient client = listener.AcceptTcpClient();
				Task.Factory.StartNew(new IngestWorker(client).ProcessRequest).LogExceptions();
			}
		}
	}
}
