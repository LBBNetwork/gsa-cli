// EalItemrEngine - the IRecordEngine bound to the transactional
// EalItemr.cs (Decision 1, landed 2026-11-09). Read surface (Phase 3):
// Open/Close/ReadRecord/ListRecords/TotalSlots/UsedSlots/Verify. Mutation
// surface (Phase 4): UpsertRecord/DeleteRecord via the library's session
// mode, Commit/Rollback at the D1 boundary, ExtendDatabase/CreateNewDatabase
// for EXTEND and CREATE NEW.
//
// Session lifecycle notes (verified against EalItemr.cs):
//   - BeginSession() resets _dirty, so re-beginning an active session would
//     silently mark staged work clean; EnsureSession() guards against that.
//   - The library's Close()/Dispose() AUTO-COMMITS a dirty session. The bridge
//     contract says close discards staged work, so Close() rolls the session
//     back first and only then disposes.
//
// Conflict rule (D46): where spec prose and the library disagree, the
// library wins - codes print in EalItemr's canonical read-back form,
// exceptions propagate verbatim for D31 prefixing.

using System;

namespace GsACli.Bridge
{
	public class EalItemrEngine : IRecordEngine
	{
		private C3.EalItemr.EalItemrFile _file;

		// Materialized list cache (D56): the library is image-resident and
		// re-parses every record on each Browse, so an un-cached ListRecords()
		// churns ~600k fresh objects per call; .NET 2.0's workstation GC does
		// not decommit that heap back to the OS, so repeated SELECT/COUNT (or
		// select/^C) cycles ratcheted the working set past 1 GB. Cache one
		// materialized copy for the life of the open database: memory stays at
		// ~one copy instead of growing per cycle, and repeat scans skip the
		// re-parse. Main-thread-only (same pattern as Abort). Invalidated on
		// Open/Close and - required - by every Phase 4 mutation entry point,
		// because D1 staged mutations must be visible in-session.
		private RecordData[] _listCache;

		// True while a library mutation session is active (BeginSession called,
		// Commit/Rollback not yet). Guards the double-BeginSession trap and lets
		// Close() discard staged work instead of auto-committing it.
		private bool _sessionActive;

		public EalItemrEngine()
		{
			this._file = null;
			this._sessionActive = false;
		}

		/// <summary>
		/// Open the database file. Lock acquisition lives in gsa-cli (D14);
		/// this is pure engine open. A malformed or unreadable file throws
		/// EalItemrException (FileFormatException) - Session prints its
		/// message verbatim after "Database Exception:" (D31/D46).
		/// </summary>
		public void Open(string databasePath)
		{
			// A re-open is a different database: any cached listing and any
			// session state are stale.
			InvalidateListCache();
			this._sessionActive = false;
			this._file = C3.EalItemr.EalItemrFile.Open(databasePath);
		}

		/// <summary>
		/// Close the engine (idempotent; discards any staged work). The library's
		/// Dispose() auto-commits a dirty session, so an active one is rolled back
		/// first - BYE/UNLOAD FORCE and EOF teardown must never persist changes.
		/// </summary>
		public void Close()
		{
			InvalidateListCache();
			if (this._file != null)
			{
				try
				{
					if (this._sessionActive)
					{
						object ignored = this._file.RollbackSessionAsync().Result; // .Result rethrows on failure
					}
				}
				catch (Exception)
				{
					// A failing rollback must not block close: the lock release in
					// Session.ReleaseDatabase is what matters (D14).
				}
				finally
				{
					this._sessionActive = false;
				}

				this._file.Dispose();
				this._file = null;
			}
		}

		/// <summary>
		/// Read one record by ITEM_CODE; null on a plain miss (the library
		/// contract - no exception for an absent code).
		/// </summary>
		public RecordData ReadRecord(string itemCode)
		{
			C3.EalItemr.EalItemrRecord? rec = this._file.GetRecordAsync(itemCode).Result;
			return rec.HasValue ? ToRecordData(rec.Value) : null;
		}

		/// <summary>
		/// Every record in block/slot scan order (the library's canonical
		/// listing; SELECT/COUNT filter from this). Serves the cached copy
		/// when one exists (D56) so repeated scans do not re-materialize.
		/// </summary>
		public RecordData[] ListRecords()
		{
			if (this._listCache != null)
			{
				return this._listCache;
			}

			C3.EalItemr.BrowseTable table = this._file.BrowseAsync().Result;
			int n = table.TotalRows;
			RecordData[] out_ = new RecordData[n];

			if (n > 0)
			{
				C3.EalItemr.EalItemrRecord[] raw = new C3.EalItemr.EalItemrRecord[n];
				table.CopyToRange(0, n, raw);
				for (int i = 0; i < n; i++)
				{
					out_[i] = ToRecordData(raw[i]);
				}
			}

			this._listCache = out_;
			return out_;
		}

		/// <summary>
		/// Drops the materialized list cache. Close/Open call this; every
		/// Phase 4 mutation entry point (UpsertRecord/DeleteRecord/Commit/
		/// Rollback) must as well, or staged changes would be invisible to
		/// SELECT/COUNT in-session (D1).
		/// </summary>
		private void InvalidateListCache()
		{
			this._listCache = null;
		}

		/// <summary>Total record slots in the file (header value).</summary>
		public int TotalSlots()
		{
			return this._file.CountRecordSlotsAsync().Result;
		}

		/// <summary>Slots currently in use.</summary>
		public int UsedSlots()
		{
			return this._file.CountUsedSlotsAsync().Result;
		}

		/// <summary>
		/// In-place integrity check (header, slot math, chains). Throws an
		/// EalItemrException on corruption; -verify prints the message
		/// verbatim and exits non-zero.
		/// </summary>
		public void Verify()
		{
			C3.EalItemr.VerifyReport report = this._file.VerifyAsync().Result;
		}

		// Mutation entry points (D1/D56): each calls InvalidateListCache() before
		// doing its work - staged mutations are visible in-session, so a cached
		// listing would hide them from SELECT/COUNT until the next open.

		/// <summary>
		/// Stage an insert or full-record rewrite (D2: INSERT is new-code only;
		/// UPDATE rewrites go through here too). Library exceptions propagate
		/// verbatim for Session's "Database Exception:" prefixing (D31).
		/// </summary>
		public void UpsertRecord(RecordData record)
		{
			InvalidateListCache(); // D56 contract
			EnsureSession();
			object ignored = this._file.WriteRecordAsync(ToLibraryRecord(record)).Result; // .Result rethrows on failure
		}

		/// <summary>Stage a deletion by ITEM_CODE (D15/D17).</summary>
		public void DeleteRecord(string itemCode)
		{
			InvalidateListCache(); // D56 contract
			EnsureSession();
			object ignored = this._file.DeleteRecordAsync(itemCode).Result; // .Result rethrows on failure
		}

		/// <summary>Make all staged changes durable (D1 commit point).</summary>
		public void Commit()
		{
			InvalidateListCache(); // D56 contract
			if (this._sessionActive)
			{
				object ignored = this._file.CommitSessionAsync().Result; // .Result rethrows on failure
				this._sessionActive = false;
			}
		}

		/// <summary>Discard staged changes; the library re-reads its disk image (D1).</summary>
		public void Rollback()
		{
			InvalidateListCache(); // D56 contract
			if (this._sessionActive)
			{
				object ignored = this._file.RollbackSessionAsync().Result; // .Result rethrows on failure
				this._sessionActive = false;
			}
		}

		/// <summary>
		/// EXTEND (D18): add capacity, return the new total slot count. The library
		/// re-reads from disk afterwards (LoadImage), which would silently discard
		// staged work - Session refuses EXTEND while changes are pending, so an
		/// active session here is a contract violation.
		/// </summary>
		public int ExtendDatabase(int additionalSlots)
		{
			if (this._sessionActive)
			{
				throw new InvalidOperationException("EXTEND with an active mutation session.");
			}

			InvalidateListCache(); // D56 contract - the image is reloaded by the library
			return this._file.ExtendDatabaseAsync(additionalSlots).Result;
		}

		/// <summary>
		/// CREATE NEW (D18): static library entry point, no open file needed.
		/// Returns the actual slot count of the created file (the layout floors to
		/// whole 10-slot data blocks, so small requests are granted more).
		/// </summary>
		public int CreateNewDatabase(string path, int requestedSlots)
		{
			return C3.EalItemr.EalItemrFile.CreateNewDatabaseAsync(requestedSlots, path).Result;
		}

		/// <summary>
		/// Begin the library mutation session exactly once. BeginSession() resets
		/// _dirty (library contract), so a second call would silently mark staged
		/// work clean and make Commit skip the flush - guard against that.
		/// </summary>
		private void EnsureSession()
		{
			if (!this._sessionActive)
			{
				this._file.BeginSession();
				this._sessionActive = true;
			}
		}

		/* ---------- record mapping (D46: surface the library's values as stored) ---------- */

		/// <summary>
		/// EalItemrRecord → RecordData, field-for-field (D55 fixed schema).
		/// Flag bits become booleans; ALL_HOURS is inverted per D5
		/// (the file stores NotAuthorizedSale). Codes pass through in the
		/// library's canonical read-back form (D24 update / D46).
		/// </summary>
		private static RecordData ToRecordData(C3.EalItemr.EalItemrRecord r)
		{
			RecordData d = new RecordData();

			d.Code = r.Code;
			d.ItemName = r.ItemName != null ? r.ItemName : string.Empty;
			d.Dept = r.Department != null ? r.Department : string.Empty;
			d.SalePriceCents = (int)r.SalePriceCents;
			d.SalePrice2Cents = (int)r.SalePrice2Cents;
			d.Qty = (byte)r.SaleQuan;

			d.Mvmt = (r.Indicat0 & C3.EalItemr.ItemFlags.MovementKept) != 0;
			d.ReqQty = (r.Indicat0 & C3.EalItemr.ItemFlags.QuantityRequired) != 0;
			d.ReqPrice = (r.Indicat0 & C3.EalItemr.ItemFlags.PriceRequired) != 0;
			d.AllHours = (r.Indicat0 & C3.EalItemr.ItemFlags.NotAuthorizedSale) == 0; // D5: file stores inverted bit
			d.RestrictedHours = (r.Indicat0 & C3.EalItemr.ItemFlags.RestrictedHours) != 0;
			d.NoReturn = (r.Indicat0 & C3.EalItemr.ItemFlags.NotReturnable) != 0;
			d.Giveaway = (r.Indicat0 & C3.EalItemr.ItemFlags.NotPricedGiveaway) != 0;

			d.Tax1 = (r.Indicat1 & C3.EalItemr.ItemFlags.TaxTable1) != 0;
			d.Tax2 = (r.Indicat1 & C3.EalItemr.ItemFlags.TaxTable2) != 0;
			d.Tax3 = (r.Indicat1 & C3.EalItemr.ItemFlags.TaxTable3) != 0;
			d.Tax4 = (r.Indicat1 & C3.EalItemr.ItemFlags.TaxTable4) != 0;

			d.MpgGroup = r.MpGroup;
			d.User = r.User != null ? r.User : string.Empty;
			d.WeightPriceReq = (r.Indicat0 & C3.EalItemr.ItemFlags.WeightPriceReq) != 0;
			d.StockNumberReq = (r.Indicat1 & C3.EalItemr.ItemFlags.StockNumberReq) != 0;
			d.Indicat1Reserved = r.Indicat1 & 0x07;

			d.PriceMethod = r.Indicat2Pricing;
			d.DcsMethod = r.Indicat2Validation;

			return d;
		}

		/// <summary>
		/// RecordData → EalItemrRecord, the exact inverse of ToRecordData: every
		/// field round-trips (D4/D37 - MpgGroup, User and the unsurfaced flag bits
		/// are preserved so UPDATE rewrites stay lossless).
		/// </summary>
		private static C3.EalItemr.EalItemrRecord ToLibraryRecord(RecordData d)
		{
			C3.EalItemr.EalItemrRecord r = new C3.EalItemr.EalItemrRecord();

			r.Code = d.Code;
			r.ItemName = d.ItemName != null ? d.ItemName : string.Empty;
			r.Department = d.Dept != null ? d.Dept : string.Empty;
			r.SalePriceCents = d.SalePriceCents;
			r.SalePrice2Cents = d.SalePrice2Cents;
			r.SaleQuan = d.Qty;
			r.MpGroup = d.MpgGroup;
			r.User = d.User != null ? d.User : string.Empty;

			byte i0 = 0;
			if (d.Mvmt) { i0 |= C3.EalItemr.ItemFlags.MovementKept; }
			if (d.ReqQty) { i0 |= C3.EalItemr.ItemFlags.QuantityRequired; }
			if (d.ReqPrice) { i0 |= C3.EalItemr.ItemFlags.PriceRequired; }
			if (!d.AllHours) { i0 |= C3.EalItemr.ItemFlags.NotAuthorizedSale; } // D5: file stores inverted bit
			if (d.RestrictedHours) { i0 |= C3.EalItemr.ItemFlags.RestrictedHours; }
			if (d.NoReturn) { i0 |= C3.EalItemr.ItemFlags.NotReturnable; }
			if (d.Giveaway) { i0 |= C3.EalItemr.ItemFlags.NotPricedGiveaway; }
			if (d.WeightPriceReq) { i0 |= C3.EalItemr.ItemFlags.WeightPriceReq; }
			r.Indicat0 = i0;

			byte i1 = 0;
			if (d.Tax1) { i1 |= C3.EalItemr.ItemFlags.TaxTable1; }
			if (d.Tax2) { i1 |= C3.EalItemr.ItemFlags.TaxTable2; }
			if (d.Tax3) { i1 |= C3.EalItemr.ItemFlags.TaxTable3; }
			if (d.Tax4) { i1 |= C3.EalItemr.ItemFlags.TaxTable4; }
			if (d.StockNumberReq) { i1 |= C3.EalItemr.ItemFlags.StockNumberReq; }
			i1 |= (byte)(d.Indicat1Reserved & 0x07);
			r.Indicat1 = i1;

			r.Indicat2Pricing = d.PriceMethod;
			r.Indicat2Validation = d.DcsMethod;

			return r;
		}
	}
}
