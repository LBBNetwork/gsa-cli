using System;
using System.Collections.Generic;

namespace GsACli.Bridge
{
	/// <summary>
	/// The transactional record-engine seam (Decision 1). Phase 4 binds the
	/// transactional EalItemr.cs through this interface; until it lands,
	/// StubRecordEngine stands in so command logic can develop freely.
	/// </summary>
	public interface IRecordEngine
	{
		/// <summary>Open the database file for staged mutation (lock acquisition lives in gsa-cli, D14).</summary>
		void Open(string databasePath);

		/// <summary>Close the database (discards any staged work, like QUIT/BYE FORCE per spec §QUIT).</summary>
		void Close();

		/// <summary>Return the record with this ITEM_CODE (staged view), or null when absent.</summary>
		RecordData ReadRecord(string itemCode);

		/// <summary>All records in file scan order (Phase 3 read surface: SELECT/COUNT).</summary>
		RecordData[] ListRecords();

		/// <summary>Total record slots in the file (header value; LOAD/SHOW DATABASE counts).</summary>
		int TotalSlots();

		/// <summary>Slots currently in use (LOAD/SHOW DATABASE counts).</summary>
		int UsedSlots();

		/// <summary>In-place integrity check; throws on corruption (-verify, Phase 3).</summary>
		void Verify();

		/// <summary>Insert or stage the update of a record (upsert, D2); missing optional fields default from settings.</summary>
		void UpsertRecord(RecordData record);

		/// <summary>Stage the deletion of one record (DROP/recode-erase path, D15).</summary>
		void DeleteRecord(string itemCode);

		/// <summary>Make all staged changes durable (D1 contract point).</summary>
		void Commit();

		/// <summary>Discard all staged changes, returning to the last committed state (D1).</summary>
		void Rollback();

		/// <summary>
		/// Extend an open database by at least this many record slots (EXTEND, D18).
		/// Returns the new total slot count actually granted (the layout rounds up
		/// in 10-slot blocks). Must not be called while a mutation session is active:
		/// the library re-reads from disk afterwards and would discard staged work.
		/// </summary>
		int ExtendDatabase(int additionalSlots);

		/// <summary>
		/// Create a brand-new database file (CREATE NEW, D18). Returns the actual
		/// slot count of the created file - the layout floors to whole 10-slot data
		/// blocks, so small requests are granted more than asked.
		/// </summary>
		int CreateNewDatabase(string path, int requestedSlots);
	}

	/// <summary>
	/// One EALITEMR record, keyed by its ITEM_CODE. Fixed-schema row (D55):
	/// named typed fields instead of a per-record Dictionary - the 600k-record
	/// materialization drops from ~1.4 GB to well under the Phase 5f target.
	/// Field names follow the spec's column set; command grammar maps onto them.
	/// </summary>
	public class RecordData
	{
		/// <summary>The record's ITEM_CODE (library canonical read-back form, D24/D46).</summary>
		public string Code;

		/// <summary>ITEM_NAME as stored (up to 18 characters).</summary>
		public string ItemName;

		/// <summary>DEPT number as stored (up to 6 digits; display/match normalize per D7/D24).</summary>
		public string Dept;

		/// <summary>SALEPRICECENTS, raw integer cents (D23).</summary>
		public int SalePriceCents;

		/// <summary>SALEPRICE2, raw integer cents; meaningful with PRICE_METHOD 2/3 (D36).</summary>
		public int SalePrice2Cents;

		/// <summary>QTY - deal quantity, 0-99 (D6/D38).</summary>
		public byte Qty;

		/// <summary>TRACK_MOVEMENT flag.</summary>
		public bool Mvmt;

		/// <summary>REQUIRE_QTY flag.</summary>
		public bool ReqQty;

		/// <summary>REQUIRE_PRICE flag.</summary>
		public bool ReqPrice;

		/// <summary>SELL_HOURS (Y = sell during normal hours; the file stores the inverted bit, D5).</summary>
		public bool AllHours;

		/// <summary>SELL_RESTRICTED flag.</summary>
		public bool RestrictedHours;

		/// <summary>NO_RETURN flag.</summary>
		public bool NoReturn;

		/// <summary>GIVEAWAY flag.</summary>
		public bool Giveaway;

		/// <summary>TAX1..TAX4 tax-table flags.</summary>
		public bool Tax1, Tax2, Tax3, Tax4;

		/// <summary>PRICE_METHOD 0-3 (D36).</summary>
		public byte PriceMethod;

		/// <summary>DCS_METHOD 0-8.</summary>
		public byte DcsMethod;

		/// <summary>
		/// MPGROUP multi-price group 0-99 (D4). Not surfaced by SELECT; carried so
		/// UPDATE rewrites stay lossless.
		/// </summary>
		public int MpgGroup;

		/// <summary>
		/// User area bytes (out of CLI scope, D4) - preserved losslessly on
		/// UPDATE/INSERT rewrites so the field is never zeroed by accident.
		/// </summary>
		public string User;

		/// <summary>
		/// WEIGHT / WeightPriceReq bit (D37: gsa-cli does not surface it) -
		/// preserved on rewrite so the underlying byte survives UPDATEs.
		/// </summary>
		public bool WeightPriceReq;

		/// <summary>INDICAT1 stock-number-required bit - preserved on rewrite.</summary>
		public bool StockNumberReq;

		/// <summary>
		/// INDICAT1 low 3 bits (0x04 = GSA Price Mgmt promo event bit, see EALDB5AC
		/// SAVEBIT; 0x02/0x01 reserved) - not surfaced by gsa-cli; preserved on
		/// rewrite so the underlying byte survives UPDATEs.
		/// </summary>
		public int Indicat1Reserved;
	}

	/// <summary>
	/// Phase 1 stand-in for the transactional EalItemr engine (Decision 1):
	/// records every call in order and stores nothing, so the dispatch and
	/// command plumbing can be exercised before the real library lands.
	/// </summary>
	public class StubRecordEngine : IRecordEngine
	{
		/// <summary>Every call, in order — useful for the Phase 6 test suite.</summary>
		public readonly List<string> Ops;

		/// <summary>Path passed to the most recent Open, or null.</summary>
		public string DatabasePath;

		public StubRecordEngine()
		{
			Ops = new List<string>();
			DatabasePath = null;
		}

		public void Open(string databasePath)
		{
			DatabasePath = databasePath;
			Ops.Add("Open(" + databasePath + ")");
		}

		public void Close()
		{
			Ops.Add("Close");
			DatabasePath = null;
		}

		public RecordData ReadRecord(string itemCode)
		{
			Ops.Add("ReadRecord(" + itemCode + ")");
			return null; // the stub stores nothing
		}

		public RecordData[] ListRecords()
		{
			Ops.Add("ListRecords");
			return new RecordData[0]; // the stub stores nothing
		}

		public int TotalSlots()
		{
			Ops.Add("TotalSlots");
			return 0;
		}

		public int UsedSlots()
		{
			Ops.Add("UsedSlots");
			return 0;
		}

		public void Verify()
		{
			Ops.Add("Verify"); // the stub has nothing to corrupt
		}

		public void UpsertRecord(RecordData record)
		{
			Ops.Add("UpsertRecord(" + record.Code + ")");
		}

		public void DeleteRecord(string itemCode)
		{
			Ops.Add("DeleteRecord(" + itemCode + ")");
		}

		public void Commit()
		{
			Ops.Add("Commit");
		}

		public void Rollback()
		{
			Ops.Add("Rollback");
		}

		public int ExtendDatabase(int additionalSlots)
		{
			Ops.Add("ExtendDatabase(" + additionalSlots + ")");
			return 0;
		}

		public int CreateNewDatabase(string path, int requestedSlots)
		{
			Ops.Add("CreateNewDatabase(" + path + ", " + requestedSlots + ")");
			return 0;
		}
	}
}
