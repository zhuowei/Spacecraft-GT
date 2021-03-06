using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Diagnostics;

namespace SpacecraftGT
{
	public class Server
	{
		public int Port;
		public bool Running;
		public string WorldName;
		public string Name;
		public string Motd;
		public string ServerHash;
		
		public Map World;
		public List<Player> PlayerList;
		public List<Window> WindowList;
		
		private TcpListener _Listener;
		
		public Server()
		{
			Port = Configuration.GetInt("port", 25565);
			Running = false;
			WorldName = Configuration.Get("world", "world");
			Name = Configuration.Get("server-name", "Minecraft Server");
			Motd = Configuration.Get("motd", "Powered by " + Color.Green + "Spacecraft");
			ServerHash = "-";
			
			World = null;
			PlayerList = new List<Player>();
			_Listener = new TcpListener(new IPEndPoint(IPAddress.Any, Port));
		}
		
		public void Run()
		{
			World = new Map(WorldName);
			World.Time = 0;
			if (!File.Exists(WorldName + "/level.dat")) {
				Spacecraft.Log("Generating world " + WorldName);
				World.Generate();
				World.ForceSave();
			}
			
			_Listener.Start();
			Spacecraft.Log("Listening on port " + Port);
			Running = true;
			
			InventoryItem i = new InventoryItem(3);
			PickupEntity e = new PickupEntity(World.SpawnX, World.SpawnY, World.SpawnZ, i);
			
			Stopwatch clock = new Stopwatch();
			clock.Start();
			double lastUpdate = 0;
			double lastGc = 0;
			
			while (Running) {
				// Check for new connections
				while (_Listener.Pending()) {
					AcceptConnection(_Listener.AcceptTcpClient());
					//Running = false;
				}
				
				if (lastUpdate + 0.2 < clock.Elapsed.TotalSeconds) {
					World.Update();
					lastUpdate = clock.Elapsed.TotalSeconds;
				}
				
				if (lastGc + 30 < clock.Elapsed.TotalSeconds) {
					GC.Collect();
				}
				
				// Rest
				Thread.Sleep(30);
			}
			
			World.ForceSave();
		}
		
		public void Spawn(Player player)
		{
			MessageAll(Color.Announce + player.Username + " has joined");
		}
		
		public void Despawn(Player player)
		{
			MessageAll(Color.Announce + player.Username + " has left");
		}
		
		public void MessageAll(string message)
		{
			foreach(Player p in PlayerList) {
				p.SendMessage(message);
			}
		}
		
		public void BlockChanged(int x, int y, int z, Block newBlock)
		{
			Chunk c = World.GetChunkAt(x, z);
			foreach(Player p in PlayerList) {
				if (p.VisibleChunks.Contains(c)) {
					p.BlockChanged(x, y, z, newBlock);
				}
			}
		}
		
		// ====================
		// Private helpers.
		
		private void AcceptConnection(TcpClient client)
		{
			Player p = new Player(client);
			PlayerList.Add(p);
		}
	}
}
