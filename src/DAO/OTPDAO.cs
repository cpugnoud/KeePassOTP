﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using KeePass;
using KeePass.Forms;
using KeePassLib;
using KeePassLib.Security;
using KeePassLib.Utility;
using PluginTools;

namespace KeePassOTP
{
	/// <summary>
	/// Data Access Object to access OTP data
	///
	/// ALWAYS USE THIS CLASS TO WORK WITH OTPDATA
	/// 
	/// This class internally differentiates between the different working modes
	///		- Store OTP data in entries
	///		- Stoer OTP data in separate datbase
	///		
	/// </summary>
	public static partial class OTPDAO //data access object
	{
		public enum OTPDefinition
		{
			None,
			Partial,
			Complete
		}
		internal struct EntryOTP
		{
			internal DateTime ValidTo;
			internal string ReadableOTP;
			internal OTPDefinition OTPDefined;
			internal KPOTP kpotp;
			internal bool Loaded;
			internal PwDatabase db;
		}
		private static Dictionary<PwEntry, EntryOTP> m_dEntryOTPData = new Dictionary<PwEntry, EntryOTP>();
		private static Dictionary<string, OTPHandler_Base> m_EntryOTPDAOHandler = new Dictionary<string, OTPHandler_Base>();
		private static string ENTRYHANDLER = string.Empty;

		public static void Init()
		{
			/* If a database is opened, it can happen that another plugin's 'FileOpened' eventhandler
			 * is executed before 'OTPDAO_FileOpened' get's executed
			 * This can result in the OTP database being overwritten and synchroning won't
			 * provide the expected results.
			 * 
			 * We try to deregister all eventhandlers, register ours and then reregister all others
			 */

			PwEntry.EntryTouched += OnEntryTouched;

			List<Delegate> lHandler = Program.MainForm.GetEventHandlers("FileOpened");
			Program.MainForm.RemoveEventHandlers("FileOpened", lHandler);
			Program.MainForm.FileOpened += OTPDAO_FileOpened;
			Program.MainForm.AddEventHandlers("FileOpened", lHandler);

			lHandler = Program.MainForm.GetEventHandlers("FileClosed");
			Program.MainForm.RemoveEventHandlers("FileClosed", lHandler);
			Program.MainForm.FileClosed += OTPDAO_FileClosed;
			Program.MainForm.AddEventHandlers("FileClosed", lHandler);
		}

		public static void Cleanup()
		{
			PwEntry.EntryTouched -= OnEntryTouched;
			Program.MainForm.FileOpened -= OTPDAO_FileOpened;
			Program.MainForm.FileClosed -= OTPDAO_FileClosed;

			m_dEntryOTPData.Clear();
			foreach (KeyValuePair<string, OTPHandler_Base> kvp in m_EntryOTPDAOHandler)
			{
				if (kvp.Value != null) kvp.Value.Cleanup();
			}
			m_EntryOTPDAOHandler.Clear();
		}

		public static OTPHandler_DB GetOTPHandler(PwDatabase db)
		{
			if (db == null) return null;
			OTPHandler_Base h;
			if (!Config.UseDBForOTPSeeds(db))
			{
				if (m_EntryOTPDAOHandler.TryGetValue(db.IOConnectionInfo.Path, out h))
					RemoveHandler(db.IOConnectionInfo.Path, true);
				return null;
			}
			h = null;
			if (m_EntryOTPDAOHandler.TryGetValue(db.IOConnectionInfo.Path, out h)) return h as OTPHandler_DB;
			h = new OTPHandler_DB();
			m_EntryOTPDAOHandler[db.IOConnectionInfo.Path] = h;
			(h as OTPHandler_DB).SetDB(db, false);
			return h as OTPHandler_DB;
		}

		public static OTPHandler_Base GetOTPHandler(PwEntry pe)
		{
			if (pe == null) return null;
			PwDatabase db = pe.GetDB();
			OTPHandler_Base h = GetOTPHandler(db);
			if (h != null) return h;
			if (!m_EntryOTPDAOHandler.ContainsKey(ENTRYHANDLER)) m_EntryOTPDAOHandler[ENTRYHANDLER] = new OTPHandler_Entry();
			return m_EntryOTPDAOHandler[ENTRYHANDLER];
		}

		public static bool RemoveHandler(string path, bool bCleanup)
		{
			OTPHandler_Base h;
			if (bCleanup && m_EntryOTPDAOHandler.TryGetValue(path, out h) && (h != null))
				h.Cleanup();
			//Remove entries
			m_dEntryOTPData = m_dEntryOTPData.Where(x => x.Value.db.IOConnectionInfo.Path != path).ToDictionary(p => p.Key, p => p.Value);
			//Remove entries of closed databases, db.IOConnection.Path is empty then since db is closed
			m_dEntryOTPData = m_dEntryOTPData.Where(x => !string.IsNullOrEmpty(x.Value.db.IOConnectionInfo.Path)).ToDictionary(p => p.Key, p => p.Value);
			return m_EntryOTPDAOHandler.Remove(path);
		}

		public static void InitEntries(PwDatabase db)
		{
			if (db == null) return;

			//Remove entries from buffer
			//by keeping only those that are contained in a different database
			int prev = m_dEntryOTPData.Count;
			m_dEntryOTPData = m_dEntryOTPData.Where(x => x.Value.db.IOConnectionInfo.Path != db.IOConnectionInfo.Path).ToDictionary(p => p.Key, p => p.Value);
			int removed = prev - m_dEntryOTPData.Count;

			PluginDebug.AddInfo("Initialized OTP entry buffer", 0, 
				"Database: " + db.IOConnectionInfo.Path, 
				"Entries removed from buffer: " + removed.ToString());
		}

		public static bool EnsureOTPSetupPossible(PwEntry pe)
		{
			OTPHandler_Base h = GetOTPHandler(pe);
			return h.EnsureOTPSetupPossible(pe);
		}

		public static bool EnsureOTPUsagePossible(PwEntry pe)
		{
			OTPHandler_Base h = GetOTPHandler(pe);
			return h.EnsureOTPUsagePossible(pe);
		}

		public static string GetReadableOTP(PwEntry pe)
		{
			OTPHandler_Base h = GetOTPHandler(pe);
			return h.GetReadableOTP(pe);
		}

		public static OTPDefinition OTPDefined(PwEntry pe)
		{
			OTPHandler_Base h = GetOTPHandler(pe);
			return h.OTPDefined(pe);
		}

		public static KPOTP GetOTP(PwEntry pe)
		{
			OTPHandler_Base h = GetOTPHandler(pe);
			return h.GetOTP(pe);
		}

		public static void SaveOTP(KPOTP myOTP, PwEntry pe)
		{
			OTPHandler_Base h = GetOTPHandler(pe);
			h.SaveOTP(myOTP, pe);
			myOTP.ResetSanitizedChange();
			Program.MainForm.RefreshEntriesList();
			bool bModified = (h is OTPHandler_DB) ? (h as OTPHandler_DB).DB == Program.MainForm.ActiveDatabase : true;
			Program.MainForm.UpdateUI(false, null, false, null,	true, null, bModified);
		}

		internal static PwDatabase GetDB(this PwEntry pe)
		{
			if (pe == null) return null;
			return Program.MainForm.DocumentManager.FindContainerOf(pe);
		}

		internal static void UpdateOTPBuffer(PwEntry pe, EntryOTP otp)
		{
			//Empty db path = db is closed
			if ((otp.db == null) || string.IsNullOrEmpty(otp.db.IOConnectionInfo.Path))
			{
				m_dEntryOTPData.Remove(pe);
				return;
			}
			m_dEntryOTPData[pe] = otp;
		}

		private static void OTPDAO_FileOpened(object sender, FileOpenedEventArgs e)
		{
			List<string> lMsg = new List<string>();
			bool bUseDB = Config.UseDBForOTPSeeds(e.Database);
			bool bPreload = Config.PreloadOTPDB(e.Database);
			if (bUseDB)
			{
				//OTP-DB has to be used
				//Now check for KeePassOTB.DB entry that needs to be migrated

				//If CustomData exists => No need to check any further
				CheckAndMigrate(e.Database, OTP_Migrations.Entry2CustomData);
				lMsg.Add("Uses OTP database: true");
				lMsg.Add("Preload OTP database: " + bPreload.ToString());
				OTPHandler_DB h = GetOTPHandler(e.Database);
				if (h == null)
				{
					lMsg.Add("Error getting OTP-DB: Handler: true");
					RemoveHandler(e.Database.IOConnectionInfo.Path, true);
				}
				h = GetOTPHandler(e.Database);
				if (h != null)
				{
					h.RememberInitialOTPDB(e.Database);
					h.SetDB(e.Database, bPreload);
				}
				else
					lMsg.Add("Error restoring OTP-DB: Handler: true");
			}
			else
			{
				CheckAndMigrate(e.Database);
				bool bRemoved = RemoveHandler(e.Database.IOConnectionInfo.Path, true);
				lMsg.Add("Uses OTP database: " + bUseDB.ToString());
				lMsg.Add("Preload OTP database: " + bPreload.ToString());
				lMsg.Add("Removed OTP database from buffer:" + bRemoved.ToString());
			}
			PluginDebug.AddInfo("Database file opened", 0, lMsg.ToArray());
		}

		[Flags]
		private enum OTP_Migrations
		{ //Update CheckAndMigrate(PwDatabase db) if changes are done here
			None = 0,
			Entry2CustomData = 1,
			KeePassOTP2OtpAuth = 2,
			SanitizeSeed = 4
		}

		public static bool CheckAndMigrate(PwDatabase db)
		{
			return CheckAndMigrate(db, OTP_Migrations.Entry2CustomData | OTP_Migrations.KeePassOTP2OtpAuth | OTP_Migrations.SanitizeSeed);
		}	

		/// <summary>
		/// Perform all kind of migrations between different KeePassOTP versions
		/// </summary>
		/// <param name="db"></param>
		/// <returns>true if something was migrated, false if nothing was done</returns>
		private static bool CheckAndMigrate(PwDatabase db, OTP_Migrations omFlags)
		{
			const string Migration_EntryDB = "KeePassOTP.MigrationStatus";
			const string Migration_OTPDB = "KeePassOTPDB.MigrationStatus";
			string sMigrationStatus = string.Empty;
			bool bMigrated = false;


			//Get DB to work on
			OTPDAO.OTPHandler_DB h = GetOTPHandler(db);
			if (h != null) sMigrationStatus = Migration_OTPDB;
			else sMigrationStatus = Migration_EntryDB;

			OTP_Migrations omStatusOld = OTP_Migrations.None;
			if (!OTP_Migrations.TryParse(db.CustomData.Get(sMigrationStatus), out omStatusOld)) omStatusOld = OTP_Migrations.None;
			OTP_Migrations omStatusNew = omStatusOld;

			if (MigrationRequired(OTP_Migrations.Entry2CustomData, omFlags, omStatusOld))
			{
				if (!db.UseDBForOTPSeeds() || !db.CustomData.Exists(OTPDAO.OTPHandler_DB.DBNAME))
				{
					PwEntry pe = OTPHandler_DB.GetOTPDBEntry(db);
					if (pe != null)
					{
						bMigrated = true;
						OTPDAO.MigrateToCustomdata(db, pe);
					}
				}
				omStatusNew |= OTP_Migrations.Entry2CustomData;
			}

			if (MigrationRequired(OTP_Migrations.KeePassOTP2OtpAuth, omFlags, omStatusOld))
			{
				int r = CheckOTPDataMigration(db);
				bMigrated |= r > 0;
				if (r >= 0) omStatusNew |= OTP_Migrations.KeePassOTP2OtpAuth;
			}

			if (MigrationRequired(OTP_Migrations.SanitizeSeed, omFlags, omStatusOld))
			{
				int r = SanitizeSeeds(db);
				bMigrated |= r > 0;
				if (r >= 0) omStatusNew |= OTP_Migrations.SanitizeSeed;
			}

			if ((omStatusNew != omStatusOld) || bMigrated)
			{
				db.CustomData.Set(sMigrationStatus, omStatusNew.ToString());
				db.SettingsChanged = DateTime.UtcNow;
				db.Modified = true;
				Program.MainForm.UpdateUI(false, null, false, null, false, null, Program.MainForm.ActiveDatabase == db);
			}
			return bMigrated;
		}

		private static bool MigrationRequired(OTP_Migrations omMigrate, OTP_Migrations omFlags, OTP_Migrations status)
		{
			if ((omMigrate & omFlags) != omMigrate) return false; //not requested
			if ((omMigrate & status) == omMigrate) return false; //already done
			return true;
		}

		private static int SanitizeSeeds(PwDatabase db)
		{
			//Get DB to work on
			PwDatabase otpdb = db;
			OTPDAO.OTPHandler_DB h = GetOTPHandler(db);
			if (h != null)
			{
				if (!h.EnsureOTPUsagePossible(null)) return -1;
				otpdb = h.OTPDB;
			}
			int i = 0;
			foreach (PwEntry pe in otpdb.RootGroup.GetEntries(true))
			{
				KPOTP otp = OTPDAO.GetOTP(pe);
				if (!otp.Valid) continue;
				otp.OTPSeed = otp.OTPSeed;
				if (otp.SanitizeChanged)
				{
					i++;
					pe.CreateBackup(otpdb);
					pe.Strings.Set(Config.OTPFIELD, otp.OTPAuthString);
				}
			}
			return i;
		}

		private static void OTPDAO_FileClosed(object sender, FileClosedEventArgs e)
		{
			RemoveHandler(e.IOConnectionInfo.Path, true);
		}

		public static void MigrateToCustomdata(PwDatabase db, PwEntry pe)
		{
			bool bPreload = !pe.Strings.Exists(Config.DBPreload) || StrUtil.StringToBool(pe.Strings.ReadSafe(Config.DBPreload));
			db.CustomData.Set(Config.DBPreload, StrUtil.BoolToString(bPreload));

			bool bUseDB = !pe.Strings.Exists(Config.DBUsage) || StrUtil.StringToBool(pe.Strings.ReadSafe(Config.DBUsage));
			db.CustomData.Set(Config.DBUsage, StrUtil.BoolToString(bUseDB));

			db.CustomData.Remove(Config.DBKeySources);
			string k = pe.Strings.ReadSafe("KPOTP.KeySources");
			if (!string.IsNullOrEmpty(k))
				db.CustomData.Set(Config.DBKeySources, k);

			if (pe.Binaries.Get(OTPDAO.OTPHandler_DB.DBNAME + ".kdbx") != null)
			{
				ProtectedBinary pbOTPDB = pe.Binaries.Get(OTPDAO.OTPHandler_DB.DBNAME + ".kdbx");
				string otpdb = OTPDAO.OTPHandler_DB.ConvertToCustomData(pbOTPDB.ReadData());
				db.CustomData.Set(OTPDAO.OTPHandler_DB.DBNAME, otpdb);
			}

			bool bDeleted = false;
			if (db.RecycleBinEnabled)
			{
				PwGroup pgRecycleBin = db.RootGroup.FindGroup(db.RecycleBinUuid, true);
				if (pgRecycleBin == null)
				{
					MethodInfo miEnsureRecycleBin = Program.MainForm.GetType().GetMethod("EnsureRecycleBin", BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
					if (miEnsureRecycleBin != null)
					{
						object[] p = new object[] { null, db, null };
						try
						{
							miEnsureRecycleBin.Invoke(null, p);
							pgRecycleBin = p[0] as PwGroup;
						}
						catch { }
					}
				}
				if (pgRecycleBin != null)
				{
					pe.ParentGroup.Entries.Remove(pe);
					pgRecycleBin.AddEntry(pe, true);
					bDeleted = true;
				}
			}
			else if (!db.RecycleBinEnabled && !bUseDB)
			{
				pe.ParentGroup.Entries.Remove(pe);
				bDeleted = true;
			}
			if (!bDeleted)
			{
				pe.Strings.Remove(Config.DBPreload);
				pe.Strings.Remove(Config.DBUsage);
				pe.Strings.Remove(Config.DBKeySources);
				pe.Binaries.Remove(OTPDAO.OTPHandler_DB.DBNAME + ".kdbx");
				pe.Touch(true);
			}

			db.Modified = true;
			db.SettingsChanged = DateTime.UtcNow;
			System.Threading.Thread tUpdate = new System.Threading.Thread(UpdateUI);
			tUpdate.Start(new object[] { bDeleted, db });
			OTPDAO.InitEntries(db);
			OTPDAO.RemoveHandler(db.IOConnectionInfo.Path, true);
		}

		private static void UpdateUI(object o)
		{
			//Ensure UI is updated, compensate KeePassOTPColumnProvider trigger
			object[] p = o as object[];
			System.Threading.Thread.Sleep(1001);
			Action update = () =>
			{
				Program.MainForm.UpdateUI(false, null, (bool)p[0], null, (bool)p[0], null, Program.MainForm.ActiveDatabase == (PwDatabase)p[1]);
			};
			Program.MainForm.Invoke(update);
		}

		private static int CheckOTPDataMigration(PwDatabase db)
		{
			const string SEED = "KeePassOTP.Seed";
			const string SETTINGS = "KeePassOTP.Settings";

			//Get DB to work on
			PwDatabase otpdb = db;
			OTPDAO.OTPHandler_DB h = GetOTPHandler(db);
			if (h != null)
			{
				if (!h.EnsureOTPUsagePossible(null)) return -1;
				otpdb = h.OTPDB;
			}

			List<PwEntry> lEntries = otpdb.RootGroup.GetEntries(true).Where(x=> x.Strings.Exists(SEED) && x.Strings.Exists(SETTINGS)).ToList();

			int migrated = 0;
			foreach (PwEntry pe in lEntries)
			{
				ProtectedString seed = pe.Strings.Get(SEED);
				string settings = pe.Strings.ReadSafe(SETTINGS);

				string title = pe.Strings.ReadSafe(PwDefs.TitleField);
				string user = pe.Strings.ReadSafe(PwDefs.UserNameField);
				if (string.IsNullOrEmpty(title)) title = PluginTranslation.PluginTranslate.PluginName;
				if (!string.IsNullOrEmpty(user)) user = ":" + user;

				KPOTP otp = ConvertOTPSettings(settings);
				otp.OTPSeed = seed;

				otp.Issuer = title;
				otp.Label = user;
				ProtectedString result = otp.OTPAuthString;

				pe.CreateBackup(db);
				pe.Strings.Remove(SEED);
				pe.Strings.Remove(SETTINGS);
				pe.Strings.Set(Config.OTPFIELD, result);
				if (h == null) pe.Touch(true);
				migrated++;
			}
			if (migrated > 0)
			{
				db.Modified = true;
				if (h == null)
					Program.MainForm.UpdateUI(false, null, false, null, false, null, db == Program.MainForm.ActiveDatabase);
			}
			return migrated;
		}

		private static KPOTP ConvertOTPSettings(string settings)
		{
			KPOTP otp = new KPOTP();
			string[] setting = settings.ToUpper().Split(';');
			if (setting.Length != 5) setting = new string[] { "30", "6", "TOTP", "BASE32", "SHA1" };

			try
			{
				otp.Type = (KPOTPType)Enum.Parse(typeof(KPOTPType), setting[2]);
			}
			catch { PluginDebug.AddError("Invalid OTP data", 0, "Error field: Type", settings.ToUpper()); }
			try
			{
				otp.Encoding = (KPOTPEncoding)Enum.Parse(typeof(KPOTPEncoding), setting[3]);
			}
			catch { PluginDebug.AddError("Invalid OTP data", 0, "Error field: Encoding", settings.ToUpper()); }
			try
			{
				otp.Hash = (KPOTPHash)Enum.Parse(typeof(KPOTPHash), setting[4]);
			}
			catch { PluginDebug.AddError("Invalid OTP data", 0, "Error field: Hash", settings.ToUpper()); }

			int i = 0;
			if (!int.TryParse(setting[0], out i))
			{
				i = 30;
				PluginDebug.AddError("Invalid OTP data", 0, "Error field: Timestep / Counter", settings.ToUpper());
			}
			if (otp.Type == KPOTPType.TOTP)
				otp.TOTPTimestep = i;
			else
				otp.HOTPCounter = i;

			if (!int.TryParse(setting[1], out i))
			{
				i = 6;
				PluginDebug.AddError("Invalid OTP data", 0, "Error field: Length", settings.ToUpper());
			}
			otp.Length = i;
			return otp;
		}


		private static bool SettingsChanged(PwEntry pw, KPOTP prev, KPOTP current, out bool OnlyCounterChanged)
		{
			bool bEquals = KPOTP.Equals(prev, current, pw.Strings.ReadSafe(PwDefs.UrlField), out OnlyCounterChanged);
			return !bEquals;
		}

		private static void OnEntryTouched(object sender, ObjectTouchedEventArgs e)
		{
			// !e.Modified = Entry was not modified => nothing to do
			PwEntry pe = e.Object as PwEntry;
			if (pe == null) return;
			OTPHandler_DB h = GetOTPHandler(pe) as OTPHandler_DB;
			if (m_dEntryOTPData.ContainsKey(pe) && (h != null))
				h.UpdateOTPData(pe, true);
			m_dEntryOTPData.Remove(pe);
		}
	}

	public static partial class OTPDAO
	{
		public class OTPHandler_Base
		{
			public bool IgnoreBuffer = false;
			public virtual bool EnsureOTPSetupPossible(PwEntry pe) { return false; }
			public virtual bool EnsureOTPUsagePossible(PwEntry pe) { return false; }
			public virtual string GetReadableOTP(PwEntry pe) { return string.Empty;  }
			public virtual OTPDefinition OTPDefined(PwEntry pe) { return OTPDefinition.None; }
			public virtual KPOTP GetOTP(PwEntry pe) { return new KPOTP(); }
			public virtual void SaveOTP(KPOTP myOTP, PwEntry pe) { }
			public virtual void Cleanup() { }

			public void InitIssuerLabel(KPOTP otp, PwEntry pe)
			{
				otp.Issuer = pe.Strings.ReadSafe(PwDefs.TitleField);
				if (string.IsNullOrEmpty(otp.Issuer)) otp.Issuer = PluginTranslation.PluginTranslate.PluginName;
				otp.Label = pe.Strings.ReadSafe(PwDefs.UserNameField);
			}
		}
	}
}
