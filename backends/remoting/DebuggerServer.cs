using System;
using System.Reflection;
using System.Runtime.Remoting;
using System.Runtime.Remoting.Channels;

using Mono.Debugger;
using Mono.Debugger.Remoting;

class Server
{
	static void Main ()
	{
		string url = "mdb://" + Environment.MachineName + ":" + Assembly.GetExecutingAssembly ().Location;

		RemotingConfiguration.RegisterWellKnownServiceType (
			typeof (DebuggerBackend), "DebuggerBackend", WellKnownObjectMode.Singleton);

		DebuggerChannel channel = new DebuggerChannel (url);
		ChannelServices.RegisterChannel (channel);

		channel.Connection.Run ();
		ChannelServices.UnregisterChannel (channel);
	}
}