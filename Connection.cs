using System;
using System.Net.Sockets;
using System.Threading;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.IO.Compression;
using System.IO;
using zlib;
using System.Diagnostics;

namespace SpacecraftGT
{
	public class Connection
	{
		public string IPString;
		
		private Thread _Thread;
		private TcpClient _Client;
		private Queue<byte[]> _TransmitQueue;
		private bool _Running;
		private byte[] _Buffer;
		private Player _Player;
		private string _QuitMsg;
		
		public Connection(TcpClient client, Player player)
		{
			_Client = client;
			IPString = _Client.Client.RemoteEndPoint.ToString();
			
			_Running = true;
			_TransmitQueue = new Queue<byte[]>();
			_Buffer = new byte[0];
			_Player = player;
			
			_Thread = new Thread(ConnectionThread);
			_Thread.Name = "SC-Player " + _Client.GetHashCode();
			_Thread.Start();
		}
		
		public void Stop()
		{
			_Running = false;
		}
		
		#region Network code

		public void Transmit(PacketType type, params object[] args)
		{
			// Spacecraft.Log("Transmitting: " + type + "(" + (byte)type + ")");
			string structure = (type == PacketType.Disconnect ? "bt" : PacketStructure.Data[(byte) type]);
			
			Builder<Byte> packet = new Builder<Byte>();
			packet.Append((byte) type);
			
			byte[] bytes;
			int current = 1;
			try {
				for (int i = 1; i < structure.Length; ++i) {
					current = i;
					switch (structure[i]) {
						case 'b':		// sbyte(1)
							packet.Append((byte) (sbyte) args[i-1]);
							break;
							
						case 's':		// short(2)
							packet.Append(BitConverter.GetBytes(IPAddress.HostToNetworkOrder((short) args[i-1])));
							break;
							
						case 'f':		// float(4)
							bytes = BitConverter.GetBytes((float) args[i-1]);
							for (int j = 3; j >= 0; --j) {
								packet.Append(bytes[j]);
							}
							//packet.Append(bytes);
							break;
							
						case 'i':		// int(4)
							packet.Append(BitConverter.GetBytes(IPAddress.HostToNetworkOrder((int) args[i-1])));
							break;
							
						case 'd':		// double(8)
							bytes = BitConverter.GetBytes((double) args[i-1]);
							for (int j = 7; j >= 0; --j) {
								packet.Append(bytes[j]);
							}
							//packet.Append(bytes);
							break;
							
						case 'l':		// long(8)
							packet.Append(BitConverter.GetBytes(IPAddress.HostToNetworkOrder((long) args[i-1])));
							break;
						
						case 't':		// string
							bytes = Encoding.BigEndianUnicode.GetBytes((string) args[i-1]);
							packet.Append(BitConverter.GetBytes(IPAddress.HostToNetworkOrder((short) (bytes.Length / 2))));
							packet.Append(bytes);
							break;

						case 'T':		// string8
							bytes = Encoding.UTF8.GetBytes((string) args[i-1]);
							packet.Append(BitConverter.GetBytes(IPAddress.HostToNetworkOrder((short) bytes.Length)));
							packet.Append(bytes);
							break;
						
						case 'x':		// byte array
							packet.Append((byte[]) args[i-1]);
							break;
					
						case 'I':		// inventory entity
							// short; if nonnegative, byte then short.
							InventoryItem item = (InventoryItem) args[i - 1];
							packet.Append(BitConverter.GetBytes(IPAddress.HostToNetworkOrder(item.Type)));
							if (item.Type >= 0) {
								packet.Append(item.Count);
								packet.Append(BitConverter.GetBytes(IPAddress.HostToNetworkOrder(item.Damage)));
							}
							break;
						
						default:
							throw new NotImplementedException("Unimplemented data type (transmit)");
					}
				}
			}
			catch (InvalidCastException) {
				Spacecraft.Log("[Error] Transmitting " + type + ": expected '" + structure[current] +
					"', got " + args[current - 1].GetType().ToString() + " for argument " + current + " (format: " + structure + ")");
				throw;
			}
			lock (_TransmitQueue) {
				_TransmitQueue.Enqueue(packet.ToArray());
			}
		}
		
		private void ConnectionThread()
		{
			Stopwatch clock = new Stopwatch();
			clock.Start();
			double lastKeepAlive = 0;
			
			while (_Running) {
				try {
					while (_TransmitQueue.Count > 0) {
						byte[] next;
						lock (_TransmitQueue) {
							next = _TransmitQueue.Dequeue();
						}
						TransmitRaw(next);
					}
					
					if (!_Client.Connected) {
						_Client.Close();
						_Running = false;
						break;
					}
					
					if (_Client.GetStream().DataAvailable) {
						IncomingData();
					}
					
					if (lastKeepAlive + 20 < clock.Elapsed.TotalSeconds) {
						Transmit(PacketType.KeepAlive);
						lastKeepAlive = clock.Elapsed.TotalSeconds;
					}
					
					Thread.Sleep(30);
				}
				catch (Exception e) {
					try {
						Disconnect("Server error: " + e.Message);
					}
					catch(Exception) {}
					Spacecraft.LogError(e);
					_Running = false;
				}
			}
			if (_Player.Spawned) {
				Spacecraft.Log(_Player.Username + " has left (" + _QuitMsg + ")");
				_Player.Despawn();
			} else {
				Spacecraft.Log("/" + IPString + " disconnected (" + _QuitMsg + ")");
			}
		}
		
		private void TransmitRaw(byte[] packet)
		{
			try {
				_Client.GetStream().Write(packet, 0, packet.Length);
			}
			catch (Exception) {
				_Client.Close();
				_Running = false;
			}
		}
		
		/* public void Disconnect(string message)
		{
			Transmit(PacketType.Disconnect, message);
		} */
		
		public void Disconnect(string message)
		{
			_QuitMsg = message;
			lock (_TransmitQueue) {
				_TransmitQueue.Clear();
			}
			Transmit(PacketType.Disconnect, message);
			lock (_TransmitQueue) {
				TransmitRaw(_TransmitQueue.Dequeue());
			}
			_Client.GetStream().Flush();
			_Client.Close();
		}
		
		private void IncomingData()
		{
			NetworkStream stream = _Client.GetStream();
			Builder<byte> buffer = new Builder<byte>();
			buffer.Append(_Buffer);
			
			while (stream.DataAvailable) {
				buffer.Append((byte) stream.ReadByte());
			}
			
			_Buffer = buffer.ToArray();
			buffer = null;
			
			while (_Buffer.Length > 0) {
				Pair<int, object[]> pair = CheckCompletePacket();
				int length = pair.First;
				if (length > 0) {
					//byte[] packet = new byte[length];
					//Array.Copy(_Buffer, packet, length);
					
					byte[] newBuffer = new byte[_Buffer.Length - length];
					Array.Copy(_Buffer, length, newBuffer, 0, _Buffer.Length - length);
					_Buffer = newBuffer;
					
					ProcessPacket(pair.Second);
				} else {
					break;
				}
			}
		}
		
		private Pair<int, object[]> CheckCompletePacket()
		{
			Pair<int, object[]> nPair = new Pair<int, object[]>(0, null);
			
			PacketType type = (PacketType) _Buffer[0];
			if (_Buffer[0] >= PacketStructure.Data.Length && _Buffer[0] != 0xFF) {
				Spacecraft.Log("Got invalid packet: " + _Buffer[0]);
				return nPair;
			} 
			
			string structure = (type == PacketType.Disconnect ? "bt" : PacketStructure.Data[_Buffer[0]]);
			int bufPos = 0;
			Builder<object> data = new Builder<object>();
			byte[] bytes = new byte[8];
			
			for (int i = 0; i < structure.Length; ++i) {
				switch (structure[i]) {
					case 'b':		// sbyte(1)
						if ((bufPos + 1) > _Buffer.Length) return nPair;
						if (i == 0)
							data.Append((byte) _Buffer[bufPos]);
						else
							data.Append((sbyte) _Buffer[bufPos]);
						bufPos += 1;
						break;
					
					case 's':		// short(2)
						if ((bufPos + 2) > _Buffer.Length) return nPair;
						data.Append((short) IPAddress.NetworkToHostOrder(BitConverter.ToInt16(_Buffer, bufPos)));
						bufPos += 2;
						break;
					
					case 'f':		// float(4)
						if ((bufPos + 4) > _Buffer.Length) return nPair;
						for (int j = 0; j < 4; ++j) {
							bytes[j] = _Buffer[bufPos + 3 - j];
						}
						data.Append((float) BitConverter.ToSingle(bytes, 0));
						bufPos += 4;
						break;
					case 'i':		// int(4)
						if ((bufPos + 4) > _Buffer.Length) return nPair;
						data.Append((int) IPAddress.NetworkToHostOrder(BitConverter.ToInt32(_Buffer, bufPos)));
						bufPos += 4;
						break;
					
					case 'd':		// double(8)
						if ((bufPos + 8) > _Buffer.Length) return nPair;
						for (int j = 0; j < 8; ++j) {
							bytes[j] = _Buffer[bufPos + 7 - j];
						}
						data.Append((double) BitConverter.ToDouble(bytes, 0));
						bufPos += 8;
						break;
					case 'l':		// long(8)
						if ((bufPos + 8) > _Buffer.Length) return nPair;
						data.Append((long) IPAddress.NetworkToHostOrder(BitConverter.ToInt64(_Buffer, bufPos)));
						bufPos += 8;
						break;
					
					case 't':		// string
						if ((bufPos + 2) > _Buffer.Length) return nPair;
						short len = (short) (IPAddress.NetworkToHostOrder(BitConverter.ToInt16(_Buffer, bufPos)) * 2);
						if ((bufPos + 2 + len) > _Buffer.Length) return nPair;
						data.Append((string) Encoding.BigEndianUnicode.GetString(_Buffer, bufPos + 2, len));
						bufPos += (2 + len);
						break;
					
					case 'T':		// string8
						if ((bufPos + 2) > _Buffer.Length) return nPair;
						short len8 = IPAddress.NetworkToHostOrder(BitConverter.ToInt16(_Buffer, bufPos));
						if ((bufPos + 2 + len8) > _Buffer.Length) return nPair;
						data.Append((string) Encoding.UTF8.GetString(_Buffer, bufPos + 2, len8));
						bufPos += (2 + len8);
						break;

					case 'I':		// inventory entity
						// short; if nonnegative, byte then short.
						InventoryItem item;
						if ((bufPos + 2) > _Buffer.Length) return nPair;
						item.Type = IPAddress.NetworkToHostOrder(BitConverter.ToInt16(_Buffer, bufPos));
						if (item.Type >= 0) {
							if ((bufPos + 3) > _Buffer.Length) return nPair;
							item.Count = _Buffer[bufPos + 2];
							item.Damage = (short) IPAddress.NetworkToHostOrder(BitConverter.ToInt16(_Buffer, bufPos + 3));
							bufPos += 5;
						} else {
							item.Count = 0;
							item.Damage = 0;
							bufPos += 2;
						}
						data.Append(item);
						break;
					
					default:
						throw new NotImplementedException("Unimplemented data type (recieve)");
				}
			}
			
			return new Pair<int, object[]>(bufPos, data.ToArray());
		}
		
		public void SendChunk(Chunk chunk)
		{
			Transmit(PacketType.PreChunk, chunk.ChunkX, chunk.ChunkZ, (sbyte) 1);
			
			byte[] uncompressed = chunk.GetBytes();
			MemoryStream mem = new MemoryStream();
			ZOutputStream stream = new ZOutputStream(mem, zlibConst.Z_BEST_COMPRESSION);
			stream.Write(uncompressed, 0, uncompressed.Length);
			stream.Close();
			byte[] data = mem.ToArray();
			
			Transmit(PacketType.MapChunk, 16 * chunk.ChunkX, (short) 0, 16 * chunk.ChunkZ,
				(sbyte) 15, (sbyte) 127, (sbyte) 15, data.Length, data);
		}
		
		#endregion
		
		private void ProcessPacket(object[] packet)
		{
			PacketType type = (PacketType) (byte) packet[0];
			
			switch(type) {
				case PacketType.Handshake: {
					_Player.Username = (string) packet[1];
					Transmit(PacketType.Handshake, Spacecraft.Server.ServerHash);
					break;
				}
				case PacketType.LoginDetails: {
					int protocol = (int) packet[1];
					if (protocol != Spacecraft.ProtocolVersion) {
						Spacecraft.Log("Expecting protocol v" + Spacecraft.ProtocolVersion + ", got v" + (int) packet[1]);
						if (protocol > Spacecraft.ProtocolVersion) {
							Disconnect("Outdated server!");
						} else {
							Disconnect("Outdated client!");
						}
						break;
					}
					if ((string) packet[2] != _Player.Username) {
						Spacecraft.Log("Usernames did not match: Handshake=" + _Player.Username + ", Login=" + (string)packet[2]);
						Disconnect("Usernames did not match");
						break;
					}
					
					// TODO: Implement name verification
					
					Transmit(PacketType.LoginDetails, _Player.EntityID,
						Spacecraft.Server.Name, 
						/* World.Seed */ (long) 0, /* World.Dimension */ (sbyte) 0);
					Spacecraft.Log(_Player.Username + " (/" + IPString + ") has joined");
					_Player.Spawn();
					
					break;
				}
				
				case PacketType.Message: {
					_Player.RecvMessage((string) packet[1]);
					break;
				}
				
				case PacketType.Player: {
					// Ignore.
					break;
				}
				case PacketType.PlayerPosition: {
					_Player.X = (double) packet[1];
					_Player.Y = (double) packet[2];
					//
					_Player.Z = (double) packet[4];
					//
					break;
				}
				case PacketType.PlayerLook: {
					// TODO: Figure out this and PlayerPositionLook (see issue #1)
					float yaw = (float) packet[1], pitch = (float) packet[2];
					_Player.Yaw = (sbyte) (yaw * 256 / 360);
					_Player.Pitch = (sbyte) (pitch * 256 / 360);
					break;
				}
				case PacketType.PlayerPositionLook: {
					_Player.X = (double) packet[1];
					_Player.Y = (double) packet[2];
					// TODO: Do something with stance maybe.
					_Player.Z = (double) packet[4];
					float yaw = (float) packet[5], pitch = (float) packet[6];
					_Player.Yaw = (sbyte) (yaw * 256 / 360);
					_Player.Pitch = (sbyte) (pitch * 256 / 360);
					break;
				}
				
				case PacketType.Disconnect: {
					Disconnect("Quitting");
					break;
				}
				
				case PacketType.PlayerDigging: {
					sbyte status = (sbyte) packet[1];
					int x = (int) packet[2];
					sbyte y = (sbyte) packet[3];
					int z = (int) packet[4];
					sbyte face = (sbyte) packet[5];
					if (status == 0) {
						Chunk c = Spacecraft.Server.World.GetChunkAt(x, z);
						Pair<int, int> pos = c.GetChunkPos(x, z);
						Block b = c.GetBlock(pos.First, y, pos.Second);
						if (b == Block.Air) break;
						// TODO: Ensure player can actually destroy this block.
						c.SetBlock(pos.First, y, pos.Second, Block.Air);
						Spacecraft.Server.BlockChanged(x, y, z, Block.Air);
						new PickupEntity(x, y, z, new InventoryItem((short) b));
					}
					break;
				}
				
				case PacketType.PlayerBlockPlace: {
					int x = (int) packet[1];
					sbyte y = (sbyte) packet[2];
					int z = (int) packet[3];
					sbyte face = (sbyte) packet[4];
					InventoryItem block = (InventoryItem) packet[5];
					
					GetOffsetPos(ref x, ref y, ref z, face);
					
					InventoryItem i = _Player.Inventory.slots[36 + _Player.SlotSelected];
					if (block.Type != i.Type) break;
					if (i.Type <= 0 || i.Type >= 256) break;
					
					Chunk c = Spacecraft.Server.World.GetChunkAt(x, z);
					Pair<int, int> pos = c.GetChunkPos(x, z);
					c.SetBlock(pos.First, y, pos.Second, (Block) i.Type);
					Spacecraft.Server.BlockChanged(x, y, z, (Block) i.Type);
					
					if (--i.Count == 0) {
						i.Type = -1;
						i.Damage = 0;
					}
					_Player.Inventory.SetSlot((short)(36 + _Player.SlotSelected), i);
					
					break;
				}
				
				case PacketType.WindowClick: {
					sbyte id = (sbyte) packet[1];
					short slot = (short) packet[2];
					byte rclick = (byte) (sbyte) packet[3];
					short action = (short) packet[4];
					InventoryItem item = (InventoryItem) packet[5];
					
					bool success = false;
					if (id == 0) {
						success = _Player.Inventory.Click(_Player, slot, rclick, item);
					} else {
						foreach (Window w in Spacecraft.Server.WindowList) {
							if (w.ID == (byte) id) {
								success = w.Click(_Player, slot, rclick, item);
								break;
							}
						}
					}
					Transmit(PacketType.Transaction, id, action, (sbyte) (success ? 1 : 0));
					
					break;
				}
				
				case PacketType.CloseWindow: {
					sbyte id = (sbyte) packet[1];
					if (id == 0) {
						_Player.Inventory.Close(_Player);
					} else {
						foreach (Window w in Spacecraft.Server.WindowList) {
							if (w.ID == (byte) id) {
								w.Close(_Player);
								break;
							}
						}
					}
					break;
				}
				
				case PacketType.PlayerHolding: {
					_Player.SlotSelected = (int) (short) packet[1];
					break;
				}
				
				default: {
					Spacecraft.Log("[Packet] " + _Player.Username + " sent " + type);
					break;
				}
			}
		}
		
		private void GetOffsetPos(ref int x, ref sbyte y, ref int z, int face) {
			switch(face) {
				case 0: --y; break;
				case 1: ++y; break;
				case 2: --z; break;
				case 3: ++z; break;
				case 4: --x; break;
				case 5: ++x; break;
				default: break;
			}
		}
	}
}
