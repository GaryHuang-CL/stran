﻿using System;
using System.Collections.Generic;
using System.Text;
using System.Net;
using System.IO;
using System.Linq;
using System.Web;
using System.Text.RegularExpressions;
using MySql.Data.MySqlClient;
using System.Timers;
using Timer = System.Timers.Timer;
using System.Threading;

namespace PoproTracker
{
	public class BEException : Exception
	{
		public BEException(string message)
			: base(message)
		{
		}
		public override string ToString()
		{
			BEDict dict = new Dictionary<BEString, IBE> { { "failure reason", (BEString)Message } };
			return dict.Dump();
		}
	}
	public class PoproMod// : HttpModule
	{
		const string HashPlace = "####################";
		bool AllowUnregisteredTorrent = true;
		Dictionary<string, Dictionary<string, PeerInfo>> PeerList = new Dictionary<string, Dictionary<string, PeerInfo>>();
		Dictionary<string, TFileInfo> RegisteredTorrents = new Dictionary<string, TFileInfo>();
		MySqlConnection DB;
		Timer Tick;
		long AnnounceCount = 0, ScrapeCount = 0;

		public PoproMod()
		{
			if (!File.Exists("SQL.txt"))
				throw new IOException("SQL Connection String should be in SQL.txt");
			var ConnStr = File.ReadAllText("SQL.txt");
			DB = new MySqlConnection(ConnStr);
			DB.Open();
			Tick = new Timer(60000);
			Tick.Elapsed += new ElapsedEventHandler((sender, e) => LoadRegisteredTorrent());
			Tick.Enabled = true;
			var cmd = DB.CreateCommand();
			cmd.CommandText = "UPDATE fileinfo SET leechers = 0, seeders = 0";
			cmd.ExecuteNonQuery();
			LoadRegisteredTorrent();
		}

		public void Shutdown()
		{
			LoadRegisteredTorrent();
		}

		public void LoadRegisteredTorrent()
		{
			Debug("DB data exchange...");
			if (Monitor.TryEnter(RegisteredTorrents, 1))
				Monitor.Exit(RegisteredTorrents);
			else
			{
				Debug("DB data thread locking, exit.");
				return;
			}
			lock (RegisteredTorrents)
			{
				// save to db
				if (RegisteredTorrents.Count > 0)
				{
					var scmd = DB.CreateCommand();
					scmd.CommandText = "UPDATE fileinfo SET completed = completed + @c, leechers = @l, seeders = @s WHERE fid = @f";
					scmd.Prepare();
					scmd.Parameters.Add(new MySqlParameter("@c", MySqlDbType.Int32));
					scmd.Parameters.Add(new MySqlParameter("@l", MySqlDbType.Int32));
					scmd.Parameters.Add(new MySqlParameter("@s", MySqlDbType.Int32));
					scmd.Parameters.Add(new MySqlParameter("@f", MySqlDbType.Int32));
					foreach (var fi in RegisteredTorrents)
					{
						if (fi.Value.fid > 0 && (fi.Value.newcompleted > 0 || fi.Value.Leeching > 0 || fi.Value.Seeding > 0))
						{
							scmd.Parameters[0].Value = fi.Value.newcompleted;
							scmd.Parameters[1].Value = fi.Value.Leeching;
							scmd.Parameters[2].Value = fi.Value.Seeding;
							scmd.Parameters[3].Value = fi.Value.fid;
							scmd.ExecuteNonQuery();
							fi.Value.newcompleted = 0;
						}
					}
				}

				var cmd = DB.CreateCommand();
				cmd.CommandText = "SELECT fid, hash, completed FROM fileinfo";
				var reader = cmd.ExecuteReader();
				RegisteredTorrents.Clear();
				while (reader.Read())
				{
					var hash = reader.GetString("hash");
					if (AllowUnregisteredTorrent && RegisteredTorrents.ContainsKey(hash))
					{
						RegisteredTorrents[hash].fid = reader.GetInt32("fid");
						RegisteredTorrents[hash].Completed = reader.GetInt32("completed");
					}
					else
						RegisteredTorrents[hash] = new TFileInfo
						{
							fid = reader.GetInt32("fid"),
							hash = hash,
							Completed = reader.GetInt32("completed")
						};
				}
				reader.Close();
			}
			Debug("DB data exchange over.");
		}

		BEDict TrackerRouter(string Action, Dictionary<string, string> Get, string IP, string info_hash)
		{
			if (Action.StartsWith("announce"))
			{
				AnnounceCount++;
				if (AnnounceCount % 50 == 0)
					Console.Title = string.Format("Tracker Status: A {0}   S {1}", AnnounceCount, ScrapeCount);
				var Passkey = Get.ContainsKey("passkey") ? Get["passkey"] : "";
				if (info_hash == null)
					throw new BEException("No info_hash provided.");
				info_hash = BitConverter.ToString(HttpUtility.UrlDecodeToBytes(Encoding.ASCII.GetBytes(info_hash))).Replace("-", "").ToLower();
				if (info_hash.Length != 40)
					throw new BEException("Invalid info_hash length.");
				if (!Get.ContainsKey("peer_id"))
					throw new BEException("No peer_id provided.");
				var peer_id = Get["peer_id"];
				lock (RegisteredTorrents)
				{
					if (!RegisteredTorrents.ContainsKey(info_hash))
					{
						if (!AllowUnregisteredTorrent)
							throw new BEException("Unregistered torrent.");
						else
							RegisteredTorrents[info_hash] = new TFileInfo { fid = 0, hash = info_hash };
					}
				}
				var _xfi = RegisteredTorrents[info_hash];
				lock (PeerList)
				{
					if (!PeerList.ContainsKey(info_hash))
						PeerList[info_hash] = new Dictionary<string, PeerInfo>();
				}

				var ThisPeerList = PeerList[info_hash];
				var xevent = Get.ContainsKey("event") ? Get["event"] : "";
				var peers = new List<IBE>();
				int numwant, Port;
				try
				{
					numwant = Convert.ToInt32(Get["numwant"]);
				}
				catch (Exception)
				{
					numwant = 50;
				}
				try
				{
					Port = Convert.ToInt32(Get["port"]);
				}
				catch (Exception)
				{
					Port = 0;
				}
				var nopeerid = Get.ContainsKey("no_peer_id") && (Get["no_peer_id"] == "1");
				long left;
				try
				{
					left = Convert.ToInt32(Get["left"]);
				}
				catch (Exception)
				{
					left = 0;
				}
				lock (ThisPeerList)
				{
					if (xevent == "stopped")
					{
						if (ThisPeerList.ContainsKey(peer_id))
						{
							if (ThisPeerList[peer_id].Left == 0)
								_xfi.Seeding--;
							else
								_xfi.Leeching--;
							if (ThisPeerList[peer_id].Left != 0 && left == 0)
								_xfi.newcompleted++;
							ThisPeerList.Remove(peer_id);
						}
					}
					else
					{
						if (ThisPeerList.ContainsKey(peer_id))
						{
							ThisPeerList[peer_id].IP = IP;
							ThisPeerList[peer_id].Port = Port;
							ThisPeerList[peer_id].Left = left;
						}
						else
						{
							ThisPeerList[peer_id] = new PeerInfo
							{
								PeerId = peer_id,
								Left = left,
								IP = IP,
								Port = Port
							};
						}
						if (left == 0)
							_xfi.Seeding++;
						else
							_xfi.Leeching++;
						if (xevent == "completed")
							_xfi.newcompleted++;
						if (numwant > 0)
						{
							IEnumerable<KeyValuePair<String, PeerInfo>> rawpeers;
							if (xevent == "completed" || left == 0)
								rawpeers = ThisPeerList.OrderBy(x => x.Value.Left).Reverse().Take(numwant);
							else
								rawpeers = ThisPeerList.Where(x => x.Value.Left != 0).OrderBy(x => x.Value.Left).Reverse().Take(numwant);
							foreach (var rawpeer in rawpeers)
							{
								Dictionary<BEString, IBE> Peer = new Dictionary<BEString, IBE>();
								if (nopeerid)
									Peer["peer id"] = (BEString)rawpeer.Key;
								Peer["ip"] = (BEString)rawpeer.Value.IP;
								Peer["port"] = (BENumber)rawpeer.Value.Port;
								peers.Add((BEDict)Peer);
							}
						}
					}
				}


				var response = new Dictionary<BEString, IBE> {
					{"complete", (BENumber)_xfi.Seeding},
					{"incomplete", (BENumber)_xfi.Leeching},
					{"interval", (BENumber)600},
					{"min interval", (BENumber)10},
					{"peers", (BEList)peers}
				};

				return response;

				//if(PeerList.ContainsKey(
				throw new BEException("Not implemented.");
			}
			else if (Action.StartsWith("scrape"))
			{
				ScrapeCount++;
				if (ScrapeCount % 10 == 0)
					Console.Title = string.Format("Tracker Status: A {0}   S {1}", AnnounceCount, ScrapeCount);
				if (info_hash == null)
					throw new BEException("No info_hash provided.");
				info_hash = BitConverter.ToString(HttpUtility.UrlDecodeToBytes(Encoding.ASCII.GetBytes(info_hash))).Replace("-", "").ToLower();
				if (info_hash.Length != 40)
					throw new BEException("Invalid info_hash length.");
				lock (RegisteredTorrents)
				{
					if (!RegisteredTorrents.ContainsKey(info_hash))
					{
						if (!AllowUnregisteredTorrent)
							throw new BEException("Unregistered torrent.");
						else
							RegisteredTorrents[info_hash] = new TFileInfo { fid = 0, hash = info_hash };
					}
				}
				var _xfi = RegisteredTorrents[info_hash];

				var hashdata = new Dictionary<BEString, IBE> {
					{"complete", (BENumber)_xfi.Seeding},
					{"incomplete", (BENumber)_xfi.Leeching},
					{"downloaded", (BENumber)_xfi.Completed},
				};
				var files = new Dictionary<BEString, IBE> {
					{HashPlace, (BEDict)hashdata}
				};
				var response = new Dictionary<BEString, IBE> {
					{"files", (BEDict)files}
				};

				return response;

				//if(PeerList.ContainsKey(
				throw new BEException("Not implemented.");
			}

			throw new Exception(Action);
		}

		object o = new object();
		public void Debug(string Message)
		{
			lock (o)
			{
				Console.Write(DateTime.Now.ToString());
				Console.WriteLine(" :" + Message);
			}
		}
		public void MangooseProcess(MongooseConnection conn, MongooseRequestInfo ri)
		{
			string info_hash = null;
			if (ri.QueryString != null)
			{
				var m = Regex.Match(ri.QueryString, "info_hash=([^&]+)");
				if (m.Success)
					info_hash = m.Groups[1].Value;
			}
			//request.
			var UriPath = ri.UriPath;
			var UriParts = UriPath.Trim('/').Split('/');
			var Action = UriParts[0].Trim();
			try
			{
				if (UriParts.Length > 1 && (UriParts[1] == "announce" || UriParts[1] == "scrape"))
					Action = UriParts[1];
				if (Action == "" && UriPath.Contains("peer_id"))
					Action = "announce";
				var IP = (new IPAddress(ri.remote_ip)).ToString();
				var Result = TrackerRouter(Action, DecodeQueryString(ri.QueryString), IP, info_hash);
				var body = Result.Dump();
				if (body.Contains(HashPlace))
				{
					var pos = body.IndexOf(HashPlace);
					byte[] bodybytes = Encoding.UTF8.GetBytes(body);
					HttpUtility.UrlDecodeToBytes(Encoding.ASCII.GetBytes(info_hash)).CopyTo(bodybytes, pos);
					conn.Send(bodybytes);
				}
				else
					conn.Send(Encoding.UTF8.GetBytes(Result.Dump()));
			}
			catch (BEException e)
			{
				Debug(e.Message);
				conn.Send(Encoding.UTF8.GetBytes(e.ToString()));
			}
			catch (Exception e)
			{
				Debug("Exception occurred: " + UriPath);
				Debug(e.Message);
				Debug(e.StackTrace);
				conn.Send(HttpStatusCode.NotFound, new byte[] { });
			}
		}
		private Dictionary<string, string> DecodeQueryString(string QueryString)
		{
			Dictionary<string, string> rt = new Dictionary<string, string>();
			if (QueryString == null)
				return rt;
			QueryString = QueryString.TrimStart('=');
			var KeyValues = QueryString.Split('&');
			foreach (var KV in KeyValues)
			{
				var Pair = KV.Split('=');
				if (Pair.Length == 2)
					rt[Pair[0]] = Pair[1];
			}
			return rt;
		}

	}

}

/*
QueryString[info_hash] = 
QueryString[peer_id] = -UT1850
QueryString[port] = 64848
QueryString[uploaded] = 0
QueryString[downloaded] = 0
QueryString[left] = 0
QueryString[corrupt] = 0
QueryString[key] = 13A4CAE0
QueryString[event] = started
QueryString[numwant] = 200
QueryString[compact] = 1
QueryString[no_peer_id] = 1
QueryString[ipv6] = 2001:0:cf2
*/