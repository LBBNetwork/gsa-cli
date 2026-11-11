using System;

namespace BcGen
{
	/// <summary>
	/// Stateless item-code generation (D68). Every code is a pure function of
	/// (seed, ordinal) - no shared state, no locks, no used-set tracking: any
	/// worker thread can derive row i's code independently and the result is
	/// identical on every machine. Within-run duplicates are possible and
	/// accepted by owner ruling; gsa-cli rejects duplicated codes at import.
	/// </summary>
	public static class Codes
	{
		// Stream salts: two distinct well-known splitmix64 mixing constants,
		// so the rnd4 and upc-a streams never correlate for a given seed.
		private const ulong Rnd4Salt = 0x9E3779B97F4A7C15UL;
		private const ulong UpcaSalt = 0xBF58476D1CE4E5B9UL;

		/// <summary>
		/// splitmix64 finalizer: a fast, well-distributed bijection on 64-bit
		/// words. .NET 2.0 has System.UInt64 and unchecked overflow is the
		/// default for ulong arithmetic - no P/Invoke, no RNG state.
		/// </summary>
		public static ulong SplitMix64(ulong x)
		{
			x = unchecked(x + 0x9E3779B97F4A7C15UL);
			x = unchecked((x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL);
			x = unchecked((x ^ (x >> 27)) * 0x94D049BB133111EBUL);
			return x ^ (x >> 31);
		}

		/// <summary>
		/// The ordinal-th rnd4 code for a seed: uniform over the PLU space
		/// 1000..9999 (D32's rnd4 domain). Ordinal is the cell's position among
		/// same-method generations in file order.
		/// </summary>
		public static string Rnd4At(ulong seed, long ordinal)
		{
			ulong h = SplitMix64(unchecked(seed + (ulong)ordinal * 0x9E3779B97F4A7C15UL + Rnd4Salt));
			return (1000L + (long)(h % 9000UL)).ToString();
		}

		/// <summary>
		/// The ordinal-th upc-a code for a seed: uniform 11-digit base over
		/// 10^10..10^11-1 (gsa-cli's GenerateUpcA domain) plus the correct
		/// check digit, so every generated value is a valid UPC-A.
		/// </summary>
		public static string UpcAAt(ulong seed, long ordinal)
		{
			ulong h = SplitMix64(unchecked(seed + (ulong)ordinal * 0x9E3779B97F4A7C15UL + UpcaSalt));
			long base11 = 10000000000L + (long)(h % 90000000000UL);
			return ((base11 * 10) + UpcACheckDigit(base11)).ToString();
		}

		/// <summary>
		/// The ordinal-th upc-a-nc code for a seed (D70): the same UPC-A as
		/// UpcAAt with its check digit chopped off - an 11-digit base, for label
		/// software that rejects check digits. Stateless like UpcAAt; within-run
		/// duplicates remain accepted by D68's owner ruling (gsa-cli rejects them
		/// at import).
		/// </summary>
		public static string UpcANoCheckAt(ulong seed, long ordinal)
		{
			return UpcAAt(seed, ordinal).Substring(0, 11);
		}

		/// <summary>
		/// UPC-A check digit over the first 11 digits: rightmost of the 11 is
		/// an odd position (weight 3), alternating from there. Same algorithm as
		/// GsACli.Session.UpcACheckDigit - copied, not referenced, so bcgen stays
		/// a standalone binary.
		/// </summary>
		public static int UpcACheckDigit(long first11)
		{
			int sum = 0;
			long x = first11;
			bool odd = true; // rightmost of the 11 digits is an odd position (weight 3)

			for (int i = 0; i < 11; i++)
			{
				int d = (int)(x % 10);
				sum += odd ? d * 3 : d;
				x /= 10;
				odd = !odd;
			}

			return (10 - sum % 10) % 10;
		}
	}
}
