using System;
using System.Net;
using System.Net.Sockets;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace SillyDPI
{
	class DNSManager
	{
		static Dictionary<string, IPAddress> Hosts = new Dictionary<string, IPAddress>();
		static object lockObj = new object();

		public static IPAddress GetIP(string Host)
		{
			if (Hosts.ContainsKey(Host))
				return Hosts[Host];

			IPHostEntry Entry = null;
			IPAddress Resolve = null;

			#region Try Parse IP
			if (IPAddress.TryParse(Host, out Resolve))
			{
				if (Resolve.AddressFamily == AddressFamily.InterNetwork ||
					(Resolve.AddressFamily == AddressFamily.InterNetworkV6 && Regex.IsMatch(Host, @"^\[[^\[\]]+\]$")))
				{
					lock (lockObj)
					{
						if (!Hosts.ContainsKey(Host))
							Hosts.Add(Host, Resolve);
						else return Resolve;
					}
					return Resolve;
				}
			}
			#endregion

			try
			{
				Entry= Dns.GetHostEntry(Host);
			}
			catch 
#if DEBUG 
				(Exception e)
#endif
			{
				#if DEBUG
				Console.WriteLine(e);
				#endif
				return null;
			}

			if (Entry == null)
				return null;

			if (Entry.AddressList.Length == 0)
				return null;

			foreach (IPAddress ip in Entry.AddressList)
				if (ip.AddressFamily == AddressFamily.InterNetwork)
					Resolve = ip;

			if (Resolve == null)
				Resolve = Entry.AddressList[0]; // Will have to return IPv6 then.

			lock (lockObj)
			{
				if (!Hosts.ContainsKey(Host))
					Hosts.Add(Host, Resolve);
				else return Resolve;
			}

			return Resolve;
		}
	}
}
