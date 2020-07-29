/* Copyright (c) 2007 Ben Howell
 * This software is licensed under the MIT License
 * 
 * Permission is hereby granted, free of charge, to any person obtaining a 
 * copy of this software and associated documentation files (the "Software"), 
 * to deal in the Software without restriction, including without limitation 
 * the rights to use, copy, modify, merge, publish, distribute, sublicense, 
 * and/or sell copies of the Software, and to permit persons to whom the 
 * Software is furnished to do so, subject to the following conditions:
 * 
 * The above copyright notice and this permission notice shall be included in 
 * all copies or substantial portions of the Software.
 * 
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR 
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, 
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE 
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER 
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING 
 * FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER 
 * DEALINGS IN THE SOFTWARE.
 */

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Cache;
using System.Xml;
using MouseButtons = System.Windows.Forms.MouseButtons;

using Decal.Adapter;
using Decal.Adapter.Wrappers;
using System.Globalization;
using System.Drawing.Text;

using GoArrow.RouteFinding;

namespace GoArrow.Huds
{
	class DungeonHud : WindowHud
	{
        /// <summary>
        /// Loads map urls from GoArrow.dll.config instead of hard coded.
        /// </summary>
        public string DungeonListUrl = Properties.Settings.Default.DungeonListURL;
		public string MapBaseUrl = Properties.Settings.Default.DungeonBaseURL;
        const string MapNameFormat = "{0:X4}.gif";
		public int MapCacheRefreshDays = Properties.Settings.Default.MapCacheRefreshDays; // ARCAYN.COM EDIT, CHANGED THIS TO READ FROM PROPERTIES
		public int DungeonListCacheRefreshDays = Properties.Settings.Default.DungeonListCacheRefreshDays; // ARCAYN.COM EDIT, CHANGED THIS TO READ FROM PROPERTIES
		public int BlacklistAgeDays = Properties.Settings.Default.BlacklistAgeDays; // ARCAYN.COM EDIT, CHANGED THIS TO READ FROM PROPERTIES
		public string DungeonCacheFile = Properties.Settings.Default.DungeonCacheFile;  // ARCAYN.COM EDIT, this is the TXT file GoArrow uses at the end of the day.  We'll just download it later and skip the CSV.
		public string NoFurtherAttempts = "";
			
		const double ZoomBase = 1.4142135623730950488016887242097; // sqrt(2)
		const double MaxZoomFactor = 4.0;
		private static readonly Rectangle DefaultRegion = new Rectangle(50, 75, 480, 360);

		private static bool msUserNotifiedOfNoDungeonList = false;

		private static readonly Font CompassFont = new Font(FontFamily.GenericSerif, 10);
		private static readonly Brush CompassBackground = new SolidBrush(Color.FromArgb(0x7F, Color.Black));
		private static readonly StringFormat CompassStringFormat = new StringFormat(StringFormatFlags.NoClip);
		/// <summary>12x14 arrow centered at (0,0)</summary>
		private static readonly Point[] CompassArrow = {
			new Point( 0, -7),
			new Point( 6,  7),
			new Point( 0,  4),
			new Point(-6,  7),
		};

		static DungeonHud()
		{
			CompassStringFormat.Alignment = StringAlignment.Center;
			CompassStringFormat.LineAlignment = StringAlignment.Center;
		}

		// Dungeon List
		private Dictionary<int, string> mDungeonIdToName = new Dictionary<int, string>();
		private SortedDictionary<string, int> mDungeonNameToId = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
		private Dictionary<int, DateTime> mDungeonBlacklist = new Dictionary<int, DateTime>();
		private readonly string mDungeonListPath, mDungeonCachePath;

		// Map state
		private volatile Bitmap mMap;
		private volatile int mCurrentDungeon = 0;
		private int mLastAutoloadDungeon = 0;
		private int mLastPaintedDungeon = 0;
		private bool mNeedsPaintPostDownload = false;
		private double mZoomFactor = 0;
		private float mZoomMultiplier = 1;
		private float mRotationDeg = 0;
		private float mPlayerHeading = 0;
		private PointF mPlayerLocation;
		private bool mCompassPainted = false;
		private PointF mCenter = new PointF();

		// Misc
		private WebClient mMapDownloader;

		// User settings
		private bool mAutoLoadMaps = true;
		private bool mShowCompass = true;
		private bool mAutoRotateMap = false;
		private bool mMoveWithPlayer = false;
		private MouseButtons mDragButton = MouseButtons.Left | MouseButtons.Middle;

		// Events
		public event EventHandler DungeonListUpdated;
		public event EventHandler MapChanged;
		public event EventHandler<MapBlacklistedEventArgs> MapBlacklisted;

		public DungeonHud(HudManager manager)
			: base(DefaultRegion, "Dungeon Map", manager)
		{

			mDungeonCachePath = Util.FullPath(@"Dungeon Map Cache\");
			if (!Directory.Exists(mDungeonCachePath))
			{
				Directory.CreateDirectory(mDungeonCachePath);
			}
			mDungeonListPath = mDungeonCachePath + "dungeons.txt";

			if (!File.Exists(mDungeonCachePath + "dungeons.txt"))
			{
				LoadCacheTextFile(); // ARCAYN.COM EDIT, this will download the cache txt file so it won't matter if the CSV exists.
				Util.Warning("Downloaded DUNGEONS.TXT, you may need to restart to see maps.");
			}

			DownloadDungeonList(true);

			mMapDownloader = new WebClient();
			mMapDownloader.CachePolicy = new RequestCachePolicy(RequestCacheLevel.Revalidate);
			mMapDownloader.DownloadFileCompleted += new AsyncCompletedEventHandler(MapDownloader_DownloadFileCompleted);

			ClientVisibleChanged += new EventHandler(DungeonHud_ClientVisibleChanged);

			ClientMouseDown += new EventHandler<HudMouseEventArgs>(DungeonHud_ClientMouseDown);
			ClientMouseUp += new EventHandler<HudMouseEventArgs>(DungeonHud_ClientMouseUp);
			ClientMouseDrag += new EventHandler<HudMouseDragEventArgs>(DungeonHud_ClientMouseDrag);
			ClientMouseWheel += new EventHandler<HudMouseEventArgs>(DungeonHud_ClientMouseWheel);
			ClientMouseDoubleClick += new EventHandler<HudMouseEventArgs>(DungeonHud_ClientMouseDoubleClick);

			ResizeDrawMode = HudResizeDrawMode.Repaint;
			SetAlphaFading(255, 128);

			Heartbeat += new EventHandler(DungeonHud_Heartbeat);
		}

		public override void Dispose()
		{
			SaveBlacklist();
			if (mMapDownloader != null)
			{
				mMapDownloader.DownloadFileCompleted -= MapDownloader_DownloadFileCompleted;
				mMapDownloader.CancelAsync();
				mMapDownloader.Dispose();
				mMapDownloader = null;
			}
			base.Dispose();
		}

		#region Utility Functions
		public bool IsDungeon(int landblock)
		{
			return Util.IsDungeon(landblock);
			//return (landblock & 0x0000FF00) != 0;
		}

		public int GetDungeonId(int landblock)
		{
			return unchecked((int)((uint)landblock >> 16));
		}

		public int GetDungeonId(Location loc)
		{
			if (loc.DungeonId != 0)
				return loc.DungeonId;
			int id;
			if (mDungeonNameToId.TryGetValue(loc.Name, out id))
				return id;
			return 0;
		}

		public string GetDungeonNameByLandblock(int landblock)
		{
			if (IsDungeon(landblock))
			{
				return GetDungeonNameById(GetDungeonId(landblock));
			}
			return "";
		}

		public string GetDungeonNameById(int dungeonId)
		{
			string name;
			if (mDungeonIdToName.TryGetValue(dungeonId, out name))
				return name;
			return "";
		}

		public Location LandblockToLocation(int landblock, LocationDatabase locDb)
		{
			if (IsDungeon(landblock))
			{
				return DungeonIdToLocation(GetDungeonId(landblock), locDb);
			}
			return null;
		}

		public Location DungeonIdToLocation(int dungeonId, LocationDatabase locDb)
		{
			string name;
			mDungeonIdToName.TryGetValue(dungeonId, out name);

			Location nameMatch = null;
			foreach (Location loc in locDb.Locations.Values)
			{
				if (loc.DungeonId == dungeonId)
				{
					return loc;
				}
				if (nameMatch != null && StringComparer.OrdinalIgnoreCase.Equals(name, loc.Name))
				{
					nameMatch = loc;
				}
			}
			return nameMatch;
		}

		public bool DungeonMapAvailable(int dungeonId)
		{
			return mDungeonIdToName.ContainsKey(dungeonId);
		}

		public bool DungeonMapAvailable(Location loc)
		{
			int id = GetDungeonId(loc);
			if (id != 0)
				return mDungeonIdToName.ContainsKey(id);
			return false;
		}

		public void ClearCache()
		{
			DirectoryInfo cacheFolder = new DirectoryInfo(mDungeonCachePath);
			int reloadDungeon = 0;
			if (mMap != null)
			{
				reloadDungeon = CurrentDungeon;
				mMap.Dispose();
				mMap = null;
			}
			foreach (FileInfo file in cacheFolder.GetFiles())
			{
				try
				{
					file.Delete();
				}
				catch (IOException) { /* Ignore */ }
			}
			DownloadDungeonList(false);
			if (reloadDungeon != 0)
			{
				LoadDungeonById(reloadDungeon);
			}
		}
		#endregion Utility Functions

		#region Dungeon List
		private void DownloadDungeonList(bool async)
		{
			LoadBlacklist();
			LoadDungeonListFile(DungeonListCacheRefreshDays);
			if (!IsDungeonListLoaded)
			{
				WebClient dungeonDownloader = new WebClient();
				dungeonDownloader.CachePolicy = new RequestCachePolicy(RequestCacheLevel.Revalidate);
				dungeonDownloader.DownloadFileCompleted +=
					delegate(object sender, AsyncCompletedEventArgs e)
					{
						DungeonDownloader_DownloadFileCompleted(sender, e.Error, e.Cancelled);
					};
				if (async)
				{
					dungeonDownloader.DownloadFileAsync(new Uri(DungeonListUrl), mDungeonListPath);
				}
				else
				{
					Exception error = null;
					try { dungeonDownloader.DownloadFile(new Uri(DungeonListUrl), mDungeonListPath); }
					catch (Exception ex) { error = ex; }
					DungeonDownloader_DownloadFileCompleted(dungeonDownloader, error, false);
				}
			}
			else if (DungeonListUpdated != null)
			{
				DungeonListUpdated(this, EventArgs.Empty);
			}
		}

		private void DungeonDownloader_DownloadFileCompleted(object sender, Exception error, bool cancelled)
		{
			try
			{
				if (error != null)
				{
					Util.HandleException(error);
				}
				else if (!cancelled)
				{
					LoadDungeonListFile(int.MaxValue);
					if (DungeonListUpdated != null)
					{
						DungeonListUpdated(this, EventArgs.Empty);
					}
				}
				if (sender is IDisposable)
				{
					((IDisposable)sender).Dispose();
				}
			}
			catch (Exception ex) { Util.HandleException(ex); }
		}

		private void LoadDungeonListFile(int maxAgeDays)
		{
			mDungeonIdToName.Clear();
			mDungeonNameToId.Clear();

			FileInfo dungeonsFile = new FileInfo(mDungeonListPath);
			// ARCAYN.COM I just disable this and assume the file exists
			// Will probably make it download here to avoid the assumption
			// Not right now though.


			//if (dungeonsFile.Exists && DateTime.Now.Subtract(dungeonsFile.LastWriteTime).Days < maxAgeDays)
		
			//ARCAYN.COM, now we just check that the file is there and we open it.  no csv needed anymore, since I can't find it anyway.
			if (dungeonsFile.Exists)
				{
				try
				{
					using (StreamReader dungeonReader = new StreamReader(dungeonsFile.FullName))
					{
						string line;
						while ((line = dungeonReader.ReadLine()) != null)
						{
							string[] parts = line.Split(';');
							int dungeonId;
							if (parts.Length >= 2 && int.TryParse(parts[0], NumberStyles.HexNumber, null, out dungeonId))
							{
								string name = parts[1].Trim();
								mDungeonIdToName[dungeonId] = name;
								mDungeonNameToId[name] = dungeonId;
							}
						}
					}
				}
				catch (IOException) { /* Ignore */ }
			}
		}

		private void LoadCacheTextFile()
        {
			try
			{
				using (WebClient wc = new WebClient())
				{
					wc.DownloadFileAsync(
						// Param1 = Link of file
						new System.Uri(DungeonCacheFile),
						// Param2 = Path to save
						mDungeonCachePath + "dungeons.txt"
					);
				}
			}
			catch (Exception e)
            {
				//do nothing, maybe write output later that we couldn't download
				//and to load a copy from the site
            }
		}

		private void LoadBlacklist()
		{
			mDungeonBlacklist.Clear();
			FileInfo blacklistFile = new FileInfo(mDungeonCachePath + "blacklist.xml");
			if (!blacklistFile.Exists)
				return;

			XmlDocument blacklist = new XmlDocument();
			try
			{
				blacklist.Load(blacklistFile.FullName);
			}
			catch
			{
				blacklistFile.Delete();
			}

			foreach (XmlElement ele in blacklist.DocumentElement.GetElementsByTagName("dungeon"))
			{
				try
				{
					DateTime expire = new DateTime(long.Parse(ele.GetAttribute("expire")), DateTimeKind.Utc);
					if (expire > DateTime.UtcNow)
						mDungeonBlacklist[int.Parse(ele.GetAttribute("id"))] = expire;
				}
				catch { /* Ignore */ }
			}
		}

		private void SaveBlacklist()
		{
			XmlDocument blacklist = new XmlDocument();
			blacklist.AppendChild(blacklist.CreateElement("blacklist"));
			foreach (KeyValuePair<int, DateTime> kvp in mDungeonBlacklist)
			{
				XmlElement ele = (XmlElement)blacklist.DocumentElement.AppendChild(blacklist.CreateElement("dungeon"));
				ele.SetAttribute("id", kvp.Key.ToString());
				ele.SetAttribute("expire", kvp.Value.Ticks.ToString());
			}

			Util.SaveXml(blacklist, mDungeonCachePath + "blacklist.xml");
		}

		public bool IsBlacklisted(int id)
		{
			DateTime expire;
			if (mDungeonBlacklist.TryGetValue(id, out expire))
			{
				return expire > DateTime.UtcNow;
			}
			return false;
		}

		private void AddToBlacklist(int id)
		{
			mDungeonBlacklist[id] = DateTime.UtcNow + TimeSpan.FromDays(BlacklistAgeDays);
			if (MapBlacklisted != null)
			{
				MapBlacklisted(this, new MapBlacklistedEventArgs(id));
			}
		}

		public bool IsDungeonListLoaded
		{
			get { return mDungeonIdToName.Count > 0; }
		}

		public SortedDictionary<string, int> DungeonNamesAndIDs
		{
			get { return mDungeonNameToId; }
		}
		#endregion Dungeon List

		#region User Settings
		public bool AutoLoadMaps
		{
			get { return mAutoLoadMaps; }
			set { mAutoLoadMaps = value; }
		}

		public bool AutoRotateMap
		{
			get { return mAutoRotateMap; }
			set { mAutoRotateMap = value; }
		}

		public bool MoveWithPlayer
		{
			get { return mMoveWithPlayer; }
			set
			{
				mMoveWithPlayer = value;
				mPlayerLocation = new PointF();
				Repaint();
			}
		}

		public MouseButtons DragButton
		{
			get { return mDragButton; }
			set { mDragButton = value; }
		}

		private bool IsPlayerArrowVisible
		{
#if USING_D3D_CONTAINER
			get { return MoveWithPlayer && ClientVisible; }
#else
			get { return MoveWithPlayer && ClientVisible && CurrentDungeon == GetDungeonId(Host.Actions.Landcell); }
#endif
		}

		public bool ShowCompass
		{
			get { return mShowCompass; }
			set
			{
				bool visible = IsCompassVisible;
				mShowCompass = value;
				if (visible != IsCompassVisible)
				{
					Repaint();
				}
			}
		}

		private bool IsCompassVisible
		{
#if USING_D3D_CONTAINER
			get { return ShowCompass && ClientVisible; }
#else
			get { return ShowCompass && ClientVisible && CurrentDungeon == GetDungeonId(Host.Actions.Landcell); }
#endif
		}

		public int CurrentDungeon
		{
			get { return mCurrentDungeon; }
		}

		public void ResetPosition()
		{
			Region = DefaultRegion;
		}
		#endregion User Settings

		#region Map Loading
		/// <summary>If the landblock is indoors and the dungeon ID is known, load the dungeon map.</summary>
		public void LoadDungeonByLandblock(int landblock)
		{
			if (IsDungeon(landblock))
			{
				LoadDungeonById(GetDungeonId(landblock));
			}
		}

		/// <summary>If the dungeon ID is known, load the dungeon map.</summary>
		public void LoadDungeonById(int id)
		{
			mPlayerLocation = new PointF();

			if (id == 0)
			{
				mMap = null;
				mCurrentDungeon = 0;
				Repaint();
				return;
			}

			if (Disposed || !ClientVisible)
				return;

			if (!IsDungeonListLoaded)
			{
				if (!msUserNotifiedOfNoDungeonList)
				{
					msUserNotifiedOfNoDungeonList = true;
					Util.Error("Could not obtain a list of dungeons from ACMaps, but that's ok!");
					Util.Error("The Arcayn version you have downloaded dungeons.txt which is what GoArrow uses.");
					Util.Error("Close your client and relaunch, you should have dungeon maps again!");
				}
			}
			else if (!mMapDownloader.IsBusy && (mMap == null || CurrentDungeon != id)
					&& mDungeonIdToName.ContainsKey(id) && !IsBlacklisted(id))
			{

				Zoom = 1;
				mRotationDeg = 0;
				mCenter = new PointF();
				mCurrentDungeon = 0;
				Title = "Dungeon Map";
				if (mMap != null)
				{
					mMap.Dispose();
					mMap = null;
				}

				// Download the dungeon map
				if (!LoadMapFromFile(id, MapCacheRefreshDays))
				{
					// Get the map from AC Maps
					string mapName = string.Format(MapNameFormat, id);
					mMapDownloader.DownloadFileAsync(new Uri(MapBaseUrl + mapName),
						Util.FullPath(mDungeonCachePath + mapName), id);
				}
				Repaint();
			}
		}

		private bool LoadMapFromFile(int id, int maxAgeDays)
		{
			try
			{
				mPlayerLocation = new PointF();
				string mapName = string.Format(MapNameFormat, id);
				FileInfo mapFile = new FileInfo(mDungeonCachePath + mapName);
				if (mapFile.Exists && DateTime.Now.Subtract(mapFile.LastWriteTime).Days < maxAgeDays)
				{
					mMap = new Bitmap(mapFile.FullName);
					mCurrentDungeon = id;
					string dungeonName;
					if (mDungeonIdToName.TryGetValue(CurrentDungeon, out dungeonName))
						Title = "Map of " + dungeonName;
					else
						Title = "Dungeon Map";
					Repaint();
					return true;
				}
			}
			catch
			{
				mMap = null;
				mCurrentDungeon = 0;
			}
			return false;
		}

		private void MapDownloader_DownloadFileCompleted(object sender, AsyncCompletedEventArgs e)
		{
			NoFurtherAttempts = "No further attempts will be made to download it for the next " + BlacklistAgeDays + " days. Click the Clear Map Cache "
			+ "button if you want to try again before then.";

			try
			{
				mNeedsPaintPostDownload = true;
				int id = (int)e.UserState;
				if (e.Error != null)
				{
					mMapDownloader.CancelAsync();
					if (!LoadMapFromFile(id, int.MaxValue))
					{
						bool handled = false;
						if (e.Error is WebException && ((WebException)e.Error).Response is HttpWebResponse)
						{
							HttpWebResponse response = (HttpWebResponse)((WebException)e.Error).Response;
							if (response.StatusCode == HttpStatusCode.NotFound)
							{
								Util.Warning("The requested map is not available on the server (404). "
									+ NoFurtherAttempts);
								handled = true;
							}
						}
						if (!handled)
						{
							Util.HandleException(e.Error);
							Util.Warning("Failed to download dungeon map. " + NoFurtherAttempts);
						}

						AddToBlacklist(id);
					}
				}
				else if (e.Cancelled)
				{
					LoadMapFromFile(id, int.MaxValue);
				}
				else
				{
					if (!LoadMapFromFile(id, int.MaxValue))
					{
						Util.Error("The downloaded dungeon map is corrupt. " + NoFurtherAttempts);
						AddToBlacklist(id);
					}
				}
				Zoom = 1;
				mRotationDeg = 0;
				mCenter = new PointF();
			}
			catch (Exception ex) { Util.HandleException(ex); }
		}

		public bool CancelDownload()
		{
			if (mMapDownloader.IsBusy)
			{
				mMapDownloader.CancelAsync();
				Repaint();
				return true;
			}
			Repaint();
			return false;
		}
		#endregion Map Loading

		#region Event Handling
		private void DungeonHud_ClientMouseDown(object sender, HudMouseEventArgs e)
		{
			e.Eat = true;
		}

		private void DungeonHud_ClientMouseUp(object sender, HudMouseEventArgs e)
		{
			e.Eat = true;
		}

		private void DungeonHud_ClientMouseDrag(object sender, HudMouseDragEventArgs e)
		{
			if ((e.Button & DragButton) != 0)
			{
				e.Eat = true;
				Matrix t = TransformMatrix(null);
				t.Invert();
				PointF[] points = { new PointF(0, 0), new PointF(e.DeltaX, e.DeltaY) };
				t.TransformPoints(points);
				mCenter.X += points[0].X - points[1].X;
				mCenter.Y += points[0].Y - points[1].Y;

				Repaint();
			}
		}

		private void DungeonHud_ClientMouseDoubleClick(object sender, HudMouseEventArgs e)
		{
			try
			{
				if ((e.Button & DragButton) != 0)
				{
					e.Eat = true;
					// Center on point
					Matrix t = TransformMatrix(null);
					t.Invert();
					PointF[] points = { e.Location };
					t.TransformPoints(points);
					mCenter = points[0];

					Repaint();
				}
			}
			catch (Exception ex) { Util.HandleException(ex); }
		}

		private void DungeonHud_ClientMouseWheel(object sender, HudMouseEventArgs e)
		{
			e.Eat = true;
			if (mMap != null)
			{
				Matrix mOld = TransformMatrix(null);

				float origZoom = Zoom;
				ZoomFactor += e.Delta / (double)HudMouseEventArgs.WHEEL_DELTA;
				if (Zoom != origZoom)
				{
					if (!e.Shift && !e.Control)
					{
						// Keep point under mouse cursor in same spot
						Matrix mNew = TransformMatrix(null);

						SizeF tSize = GetTransformedBoundingRect(mNew, new RectangleF(new PointF(0, 0), mMap.Size)).Size;
						if (tSize.Width < ClientSize.Width && tSize.Height < ClientSize.Height)
						{
							float z = Math.Min(ClientSize.Width / (float)mMap.Width, ClientSize.Height / (float)mMap.Height);
							if (z > Zoom)
							{
								if (z > 1) { z = 1; }
								Zoom = z;
								mNew = TransformMatrix(null);
							}
						}

						mOld.Invert();
						mNew.Invert();
						PointF[] pOld = { e.Location };
						PointF[] pNew = { e.Location };
						mOld.TransformPoints(pOld);
						mNew.TransformPoints(pNew);

						mCenter.X += pOld[0].X - pNew[0].X;
						mCenter.Y += pOld[0].Y - pNew[0].Y;
					}

					Repaint();
				}
			}
		}

		private void DungeonHud_ClientVisibleChanged(object sender, EventArgs e)
		{
#if !USING_D3D_CONTAINER
			if (AutoLoadMaps)
			{
				mLastAutoloadDungeon = GetDungeonId(Host.Actions.Landcell);
				LoadDungeonByLandblock(Host.Actions.Landcell);
			}
#endif
			Repaint();
		}

		private void DungeonHud_Heartbeat(object sender, EventArgs e)
		{
#if !USING_D3D_CONTAINER
			int dungeonId = GetDungeonId(Host.Actions.Landcell);
			if (AutoLoadMaps && mLastAutoloadDungeon != dungeonId)
			{
				mLastAutoloadDungeon = dungeonId;
				LoadDungeonByLandblock(Host.Actions.Landcell);
			}
			if (mCurrentDungeon == dungeonId)
			{
				if (AutoRotateMap)
				{
					RotationDegrees = (float)Host.Actions.Heading;
				}
				if (MoveWithPlayer)
				{
					PointF newLoc = new PointF((float)Host.Actions.LocationX, (float)Host.Actions.LocationY);
					if (mPlayerLocation.IsEmpty)
					{
						mPlayerLocation = newLoc;
					}
					else
					{
						float dX = (mPlayerLocation.X - newLoc.X) * 3.0f;
						float dY = (mPlayerLocation.Y - newLoc.Y) * 3.0f;

						if (dX != 0 || dY != 0)
						{
							mCenter.X -= dX;
							mCenter.Y += dY;
							mPlayerLocation = newLoc;
							Repaint();
						}
					}
				}
			}
			if ((IsCompassVisible || IsPlayerArrowVisible)
					&& Math.Abs(mPlayerHeading - Host.Actions.Heading) > 1)
			{
				mPlayerHeading = (float)Host.Actions.Heading;
				Repaint();
			}
#endif
			if (mCompassPainted != IsCompassVisible)
			{
				Repaint();
			}

			// Handle download/paint race condition
			if (mNeedsPaintPostDownload)
			{
				Repaint();
				mNeedsPaintPostDownload = false;
			}

			if (ClientVisible && mLastPaintedDungeon != mCurrentDungeon)
			{
				Repaint();
			}
		}
		#endregion Event Handling

		#region Map Size and Orientation
		public float RotationDegrees
		{
			get { return mRotationDeg; }
			set
			{
				if (Math.Abs(mRotationDeg - value) > 1)
				{
					mRotationDeg = value;
					Repaint();
				}
			}
		}

		public float Zoom
		{
			get { return mZoomMultiplier; }
			set { ZoomFactor = Math.Log(value, ZoomBase); }
		}

		private double ZoomFactor
		{
			get { return mZoomFactor; }
			set
			{
				mZoomFactor = value;
				if (mZoomFactor > MaxZoomFactor)
					mZoomFactor = MaxZoomFactor;
				mZoomMultiplier = (float)Math.Pow(ZoomBase, Math.Floor(mZoomFactor));

				Repaint();
			}
		}

		private Matrix TransformMatrix(Matrix m)
		{
			if (m == null)
				m = new Matrix();

			m.Translate(ClientSize.Width / 2, ClientSize.Height / 2);
			m.Rotate(-RotationDegrees);
			m.Scale(Zoom, Zoom);
			m.Translate(-mCenter.X, -mCenter.Y);

			return m;
		}

		private RectangleF GetTransformedBoundingRect(Matrix transform, RectangleF r)
		{
			PointF[] pts = {
				new PointF(r.Top, r.Left),
				new PointF(r.Top, r.Right),
				new PointF(r.Bottom, r.Left),
				new	PointF(r.Bottom, r.Right)};
			transform.TransformPoints(pts);

			float minX = pts[0].X, minY = pts[0].Y, maxX = minX, maxY = minY;
			for (int i = 1; i < 4; i++)
			{
				if (pts[i].X < minX) { minX = pts[i].X; }
				if (pts[i].Y < minY) { minY = pts[i].Y; }
				if (pts[i].X > maxX) { maxX = pts[i].X; }
				if (pts[i].Y > maxY) { maxY = pts[i].Y; }
			}

			return RectangleF.FromLTRB(minX, minY, maxX, maxY);
		}
		#endregion

		#region Painting
		protected override void PaintClient(Graphics g, bool imageDataLost)
		{
			g.Clear(Clear);
			//g.InterpolationMode = InterpolationMode.HighQualityBicubic;

			if (mLastPaintedDungeon != mCurrentDungeon)
			{
				mLastPaintedDungeon = mCurrentDungeon;
				if (MapChanged != null)
				{
					MapChanged(this, EventArgs.Empty);
				}
			}

			if (mMap == null)
			{
				string message = mMapDownloader.IsBusy ? "Downloading..." : "No dungeon map selected";
				RectangleF textRect = new Rectangle(new Point(), ClientSize);
				StringFormat textFormat = new StringFormat();
				textFormat.Alignment = StringAlignment.Center;
				textFormat.LineAlignment = StringAlignment.Center;
				textFormat.Trimming = StringTrimming.EllipsisCharacter;

				g.SmoothingMode = SmoothingMode.HighQuality;
				GraphicsPath textOutline = new GraphicsPath();
				textOutline.AddString(message, FontFamily.GenericSansSerif, 0, 15, textRect, textFormat);
				Pen p = new Pen(Color.FromArgb(210, Color.Black), 2.5f);
				p.Alignment = PenAlignment.Outset;
				p.LineJoin = LineJoin.Round;
				g.DrawPath(p, textOutline);
				g.FillPath(Brushes.White, textOutline);
			}
			else
			{
				// Draw Map
				if (mCenter.IsEmpty)
				{
					mCenter = new PointF(
						Math.Min(mMap.Width, ClientSize.Width) / 2.0f,
						Math.Min(mMap.Height, ClientSize.Height) / 2.0f);
				}

				if (mCenter.X < 0)
					mCenter.X = 0;
				else if (mCenter.X > mMap.Width)
					mCenter.X = mMap.Width;

				if (mCenter.Y < 0)
					mCenter.Y = 0;
				else if (mCenter.Y > mMap.Height)
					mCenter.Y = mMap.Height;

				// Keep on pixel boundaries if at 1:1 zoom and rotated to a cardinal direction
				if (Zoom == 1 && RotationDegrees % 90 == 0)
				{
					mCenter = Point.Round(mCenter);
				}

				Matrix origTransform = g.Transform;
				g.Transform = TransformMatrix(g.Transform);
				g.DrawImage(mMap, new Point(0, 0));


				// Draw Compass
				if (IsCompassVisible)
				{
					g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
					g.SmoothingMode = SmoothingMode.AntiAlias;
					g.Transform = origTransform;

					const int Sz = 48, C = Sz / 2, D = C - 8;
					g.FillRectangle(CompassBackground, 0, 0, Sz, Sz);
					g.DrawRectangle(Pens.Black, 0, 0, Sz, Sz);

					double theta = -RotationDegrees / 180 * Math.PI;
					PointF pN = new PointF(C - (float)Math.Cos(theta + Math.PI / 2) * D, C - (float)Math.Sin(theta + Math.PI / 2) * D);
					PointF pE = new PointF(C - (float)Math.Cos(theta + Math.PI) * D, C - (float)Math.Sin(theta + Math.PI) * D);
					PointF pS = new PointF(C - (float)Math.Cos(theta - Math.PI / 2) * D, C - (float)Math.Sin(theta - Math.PI / 2) * D);
					PointF pW = new PointF(C - (float)Math.Cos(theta) * D, C - (float)Math.Sin(theta) * D);

					g.DrawString("N", CompassFont, Brushes.Gold, new RectangleF(pN.X - 8, pN.Y - 8, 16, 16), CompassStringFormat);
					g.DrawString("E", CompassFont, Brushes.Gold, new RectangleF(pE.X - 8, pE.Y - 8, 16, 16), CompassStringFormat);
					g.DrawString("S", CompassFont, Brushes.Gold, new RectangleF(pS.X - 8, pS.Y - 8, 16, 16), CompassStringFormat);
					g.DrawString("W", CompassFont, Brushes.Gold, new RectangleF(pW.X - 8, pW.Y - 8, 16, 16), CompassStringFormat);

					g.TranslateTransform(C, C);
					g.RotateTransform(mPlayerHeading - RotationDegrees);
					g.FillPolygon(Brushes.Gold, CompassArrow);
					g.DrawPolygon(new Pen(Color.Black, 1.5f), CompassArrow);

					mCompassPainted = true;
				}
				else
				{
					mCompassPainted = false;
				}

				if (IsPlayerArrowVisible)
				{
					g.Transform = origTransform;
					g.TranslateTransform(ClientSize.Width / 2.0f, ClientSize.Height / 2.0f);
					g.RotateTransform(mPlayerHeading - RotationDegrees);
					g.FillPolygon(Brushes.Gold, CompassArrow);
					g.DrawPolygon(new Pen(Color.Black, 1.5f), CompassArrow);
				}
			}
		}
		#endregion Painting
	}

	class MapBlacklistedEventArgs : EventArgs
	{
		private int mDungeonId;

		public MapBlacklistedEventArgs(int dungeonId)
		{
			mDungeonId = dungeonId;
		}

		public int DungeonId
		{
			get { return mDungeonId; }
		}
	}
}
