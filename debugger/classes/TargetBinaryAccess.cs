using System;
using System.Text;

namespace Mono.Debugger
{
	[Serializable]
	public sealed class TargetInfo
	{
		int target_int_size;
		int target_long_size;
		int target_address_size;
		bool is_bigendian;
		AddressDomain address_domain;

		internal TargetInfo (int target_int_size, int target_long_size,
				     int target_address_size, bool is_bigendian,
				     AddressDomain domain)
		{
			this.target_int_size = target_int_size;
			this.target_long_size = target_long_size;
			this.target_address_size = target_address_size;
			this.is_bigendian = is_bigendian;
			this.address_domain = domain;
		}

		public int TargetIntegerSize {
			get {
				return target_int_size;
			}
		}

		public int TargetLongIntegerSize {
			get {
				return target_long_size;
			}
		}

		public int TargetAddressSize {
			get {
				return target_address_size;
			}
		}

		public bool IsBigEndian {
			get {
				return is_bigendian;
			}
		}

		public AddressDomain AddressDomain {
			get {
				return address_domain;
			}
		}
	}

	[Serializable]
	public sealed class TargetBlob
	{
		public readonly byte[] Contents;
		public readonly TargetInfo TargetInfo;

		public TargetBlob (byte[] contents, TargetInfo target_info)
		{
			this.Contents = contents;
			this.TargetInfo = target_info;
		}

		public TargetBlob (int size, TargetInfo target_info)
		{
			this.Contents = new byte [size];
			this.TargetInfo = target_info;
		}

		public int Size {
			get { return Contents.Length; }
		}

		public TargetBinaryReader GetReader ()
		{
			return new TargetBinaryReader (this);
		}
	}

	[Serializable]
	public class TargetBinaryAccess
	{
		protected TargetBlob blob;
		protected int pos;
		protected bool swap;

		public TargetBinaryAccess (TargetBlob blob)
		{
			this.blob = blob;
			this.swap = blob.TargetInfo.IsBigEndian;
		}

		public int AddressSize {
			get {
				int address_size = blob.TargetInfo.TargetAddressSize;
				if ((address_size != 4) && (address_size != 8))
					throw new TargetMemoryException (
						"Unknown target address size " + address_size);

				return address_size;
			}
		}

		public TargetInfo TargetInfo {
			get {
				return blob.TargetInfo;
			}
		}

		public long Size {
			get {
				return blob.Contents.Length;
			}
		}

		public long Position {
			get {
				return pos;
			}

			set {
				pos = (int) value;
			}
		}

		public bool IsEof {
			get {
				return pos == blob.Contents.Length;
			}
		}

		public byte[] Contents {
			get {
				return blob.Contents;
			}
		}

		public string HexDump ()
		{
			return HexDump (blob.Contents);
		}

		public static string HexDump (byte[] data)
		{
			StringBuilder sb = new StringBuilder ();

			if (data == null)
				return "[]";

			sb.Append ("\n" + TargetAddress.FormatAddress (0) + "   ");

			for (int i = 0; i < data.Length; i++) {
				if (i > 0) {
					if ((i % 16) == 0)
						sb.Append ("\n" + TargetAddress.FormatAddress (i) + "   ");
					else if ((i % 8) == 0)
						sb.Append (" - ");
					else
						sb.Append (" ");
				}
				sb.Append (String.Format ("{1}{0:x}", data [i], data [i] >= 16 ? "" : "0"));
			}
			return sb.ToString ();
		}

		public static string HexDump (TargetAddress start, byte[] data)
		{
			StringBuilder sb = new StringBuilder ();

			sb.Append (String.Format ("{0}   ", start));

			for (int i = 0; i < data.Length; i++) {
				if (i > 0) {
					if ((i % 16) == 0) {
						start += 16;
						sb.Append (String.Format ("\n{0}   ", start));
					} else if ((i % 8) == 0)
						sb.Append (" - ");
					else
						sb.Append (" ");
				}
				sb.Append (String.Format ("{1}{0:x}", data [i], data [i] >= 16 ? "" : "0"));
			}
			return sb.ToString ();
		}
	}
}