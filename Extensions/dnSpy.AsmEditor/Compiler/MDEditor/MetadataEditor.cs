﻿/*
    Copyright (C) 2014-2018 de4dot@gmail.com

    This file is part of dnSpy

    dnSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    dnSpy is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with dnSpy.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using dnlib.DotNet;
using dnlib.DotNet.MD;

namespace dnSpy.AsmEditor.Compiler.MDEditor {
	sealed class MetadataEditor {
		readonly RawModuleBytes moduleData;
		readonly IMetaData metadata;
		readonly List<MDHeap> heaps;

		public IMetaData RealMetadata => metadata;
		public RawModuleBytes ModuleData => moduleData;

		public BlobMDHeap BlobHeap { get; }
		public GuidMDHeap GuidHeap { get; }
		public StringsMDHeap StringsHeap { get; }
		public USMDHeap USHeap { get; }
		public TablesMDHeap TablesHeap { get; }

		public MetadataEditor(RawModuleBytes moduleData, IMetaData metadata) {
			this.moduleData = moduleData ?? throw new ArgumentNullException(nameof(moduleData));
			this.metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));

			heaps = new List<MDHeap>(metadata.AllStreams.Count);
			foreach (var stream in metadata.AllStreams) {
				switch (stream) {
				case BlobStream blobStream:
					heaps.Add(BlobHeap = new BlobMDHeap(this, blobStream));
					break;

				case GuidStream guidStream:
					heaps.Add(GuidHeap = new GuidMDHeap(this, guidStream));
					break;

				case StringsStream stringsStream:
					heaps.Add(StringsHeap = new StringsMDHeap(this, stringsStream));
					break;

				case USStream usStream:
					heaps.Add(USHeap = new USMDHeap(this, usStream));
					break;

				case TablesStream tablesStream:
					heaps.Add(TablesHeap = new TablesMDHeap(this, tablesStream));
					break;

				default:
					heaps.Add(new UnknownMDHeap(this, stream));
					break;
				}
			}
			if (BlobHeap == null)
				heaps.Add(BlobHeap = new BlobMDHeap(this, metadata.BlobStream));
			if (GuidHeap == null)
				heaps.Add(GuidHeap = new GuidMDHeap(this, metadata.GuidStream));
			if (StringsHeap == null)
				heaps.Add(StringsHeap = new StringsMDHeap(this, metadata.StringsStream));
			if (USHeap == null)
				heaps.Add(USHeap = new USMDHeap(this, metadata.USStream));
			if (TablesHeap == null)
				throw new InvalidOperationException();
		}

		public uint CreateAssemblyRef(IAssembly assembly) {
			var rid = TablesHeap.AssemblyRefTable.Create();
			var row = TablesHeap.AssemblyRefTable.Get(rid);
			row.MajorVersion = (ushort)assembly.Version.Major;
			row.MinorVersion = (ushort)assembly.Version.Minor;
			row.BuildNumber = (ushort)assembly.Version.Build;
			row.RevisionNumber = (ushort)assembly.Version.Revision;
			row.Flags = (uint)assembly.Attributes;
			row.PublicKeyOrToken = BlobHeap.Create(GetPublicKeyOrTokenBytes(assembly.PublicKeyOrToken));
			row.Name = StringsHeap.Create(assembly.Name);
			row.Locale = StringsHeap.Create(assembly.Culture);
			row.HashValue = BlobHeap.Create((assembly as AssemblyRef)?.Hash);
			return rid;
		}

		static byte[] GetPublicKeyOrTokenBytes(PublicKeyBase pkb) {
			if (pkb is PublicKey pk)
				return pk.Data;
			if (pkb is PublicKeyToken pkt)
				return pkt.Data;
			return null;
		}

		public bool MustRewriteMetadata() {
			foreach (var heap in heaps) {
				if (heap.MustRewriteHeap())
					return true;
			}
			return false;
		}
	}
}
