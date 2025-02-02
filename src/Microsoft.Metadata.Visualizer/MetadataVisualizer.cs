﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using Microsoft.CodeAnalysis.Debugging;
using Roslyn.Utilities;

namespace Microsoft.Metadata.Tools
{
    [Flags]
    public enum MetadataVisualizerOptions
    {
        None = 0,
        ShortenBlobs = 1,
        NoHeapReferences = 1 << 1
    }

    public sealed partial class MetadataVisualizer
    {
        private const string BadMetadataStr = "<bad metadata>";

        private enum BlobKind
        {
            None,
            Key,
            FileHash,

            MethodSignature,
            FieldSignature,
            MemberRefSignature,
            StandAloneSignature,

            TypeSpec,
            MethodSpec,

            ConstantValue,
            Marshalling,
            PermissionSet,
            CustomAttribute,

            DocumentName,
            DocumentHash,
            SequencePoints,
            Imports,
            ImportAlias,
            ImportNamespace,
            LocalConstantSignature,
            CustomDebugInformation,

            Count
        }

        private sealed class TableBuilder
        {
            private readonly string _title;
            private readonly string[] _header;
            private readonly List<(string[] fields, string details)> _rows;

            public char HorizontalSeparatorChar = '=';
            public string Indent = "";
            public int FirstRowNumber = 1;

            public TableBuilder(string title, params string[] header)
            {
                _rows = new List<(string[] fields, string details)>();
                _title = title;
                _header = header;
            }

            public int RowCount
                => _rows.Count;

            public void AddRow(params string[] fields)
                => AddRowWithDetails(fields, details: null);

            public void AddRowWithDetails(string[] fields, string details)
            {
                Debug.Assert(_header.Length == fields.Length);
                _rows.Add((fields, details));
            }

            public void WriteTo(TextWriter writer)
            {
                if (_rows.Count == 0)
                {
                    return;
                }

                if (_title != null)
                {
                    writer.Write(Indent);
                    writer.WriteLine(_title);
                }

                string columnSeparator = "  ";
                var columnWidths = new int[_rows.First().fields.Length];

                void updateColumnWidths( string[] fields)
                {
                    for (int i = 0; i < fields.Length; i++)
                    {
                        columnWidths[i] = Math.Max(columnWidths[i], fields[i].Length + columnSeparator.Length);
                    }
                }

                updateColumnWidths(_header);

                foreach (var (fields, _) in _rows)
                {
                    updateColumnWidths(fields);
                }

                void writeRow(string[] fields)
                {
                    for (int i = 0; i < fields.Length; i++)
                    {
                        var field = fields[i];

                        writer.Write(field);
                        writer.Write(new string(' ', columnWidths[i] - field.Length));
                    }
                }

                // header:
                int rowNumberWidth = (FirstRowNumber + _rows.Count - 1).ToString("x").Length;
                int tableWidth = Math.Max(_title?.Length ?? 0, columnWidths.Sum() + columnWidths.Length);
                string horizontalSeparator = new string(HorizontalSeparatorChar, tableWidth);

                writer.Write(Indent);
                writer.WriteLine(horizontalSeparator);

                writer.Write(Indent);
                writer.Write(new string(' ', rowNumberWidth + 2));

                writeRow(_header);

                writer.WriteLine();
                writer.Write(Indent);
                writer.WriteLine(horizontalSeparator);

                // rows:
                int rowNumber = FirstRowNumber;
                foreach (var (fields, details) in _rows)
                {
                    string rowNumberStr = rowNumber.ToString("x");
                    writer.Write(Indent);
                    writer.Write(new string(' ', rowNumberWidth - rowNumberStr.Length));
                    writer.Write(rowNumberStr);
                    writer.Write(": ");

                    writeRow(fields);
                    writer.WriteLine();

                    if (details != null)
                    {
                        writer.Write(Indent);
                        writer.Write(details);
                    }

                    rowNumber++;
                }
            }
        }

        private readonly TextWriter _writer;
        private readonly IReadOnlyList<MetadataReader> _readers;
        private readonly MetadataAggregator _aggregator;
        private readonly MetadataVisualizerOptions _options;
        private readonly SignatureVisualizer _signatureVisualizer;

        // enc map for each delta reader
        private readonly ImmutableArray<ImmutableArray<EntityHandle>> _encMaps;

        private MetadataReader _reader;
        private readonly Dictionary<BlobHandle, BlobKind> _blobKinds = new Dictionary<BlobHandle, BlobKind>();

        private MetadataVisualizer(TextWriter writer, IReadOnlyList<MetadataReader> readers, MetadataVisualizerOptions options = MetadataVisualizerOptions.None)
        {
            _writer = writer ?? throw new ArgumentNullException(nameof(writer));
            _readers = readers ?? throw new ArgumentNullException(nameof(readers));
            _options = options;
            _signatureVisualizer = new SignatureVisualizer(this);

            if (readers.Count > 1)
            {
                var deltaReaders = new List<MetadataReader>(readers.Skip(1));
                _aggregator = new MetadataAggregator(readers[0], deltaReaders);

                _encMaps = ImmutableArray.CreateRange(deltaReaders.Select(reader => ImmutableArray.CreateRange(reader.GetEditAndContinueMapEntries())));
            }
        }

        public MetadataVisualizer(MetadataReader reader, TextWriter writer, MetadataVisualizerOptions options = MetadataVisualizerOptions.None)
            : this(writer, new[] { reader ?? throw new ArgumentNullException(nameof(reader)) }, options)
        {
            _reader = reader;
        }

        public MetadataVisualizer(IReadOnlyList<MetadataReader> readers, TextWriter writer, MetadataVisualizerOptions options = MetadataVisualizerOptions.None)
            : this(writer, readers, options)
        {
        }

        private bool NoHeapReferences => (_options & MetadataVisualizerOptions.NoHeapReferences) != 0;

        public void VisualizeAllGenerations()
        {
            for (int i = 0; i < _readers.Count; i++)
            {
                _writer.WriteLine(">>>");
                _writer.WriteLine($">>> Generation {i}:");
                _writer.WriteLine(">>>");
                _writer.WriteLine();

                Visualize(i);
            }
        }

        public void Visualize(int generation = -1)
        {
            _reader = (generation >= 0) ? _readers[generation] : _readers[_readers.Count - 1];

            WriteModule();
            WriteTypeRef();
            WriteTypeDef();
            WriteField();
            WriteMethod();
            WriteParam();
            WriteMemberRef();
            WriteConstant();
            WriteCustomAttribute();
            WriteDeclSecurity();
            WriteStandAloneSig();
            WriteEvent();
            WriteProperty();
            WriteMethodImpl();
            WriteModuleRef();
            WriteTypeSpec();
            WriteEnCLog();
            WriteEnCMap();
            WriteAssembly();
            WriteAssemblyRef();
            WriteFile();
            WriteExportedType();
            WriteManifestResource();
            WriteGenericParam();
            WriteMethodSpec();
            WriteGenericParamConstraint();

            // debug tables:
            WriteDocument();
            WriteMethodDebugInformation();
            WriteLocalScope();
            WriteLocalVariable();
            WriteLocalConstant();
            WriteImportScope();
            WriteCustomDebugInformation();

            // heaps:
            WriteUserStrings();
            WriteStrings();
            WriteBlobs();
            WriteGuids();
        }

        private bool IsDelta => _reader.GetTableRowCount(TableIndex.EncLog) > 0;

        private string MakeTableName(TableIndex index)
            => $"{index} (index: 0x{(byte)index:X2}, size: {_reader.GetTableRowCount(index) * _reader.GetTableRowSize(index)}): ";

        private void WriteTable(TableBuilder table)
        {
            if (table.RowCount > 0)
            {
                table.WriteTo(_writer);
                _writer.WriteLine();
            }
        }

        private EntityHandle GetAggregateHandle(EntityHandle generationHandle, int generation)
        {
            var encMap = _encMaps[generation - 1];

            if (!TryGetHandleRange(encMap, generationHandle.Kind, out int start, out _))
            {
                throw new BadImageFormatException(string.Format("EncMap is missing record for {0:8X}.", MetadataTokens.GetToken(generationHandle)));
            }

            return encMap[start + MetadataTokens.GetRowNumber(generationHandle) - 1];
        }

        private static bool TryGetHandleRange(ImmutableArray<EntityHandle> handles, HandleKind handleType, out int start, out int count)
        {
            TableIndex tableIndex;
            MetadataTokens.TryGetTableIndex(handleType, out tableIndex);

            int mapIndex = handles.BinarySearch(MetadataTokens.Handle(tableIndex, 0), TokenTypeComparer.Instance);
            if (mapIndex < 0)
            {
                start = 0;
                count = 0;
                return false;
            }

            int s = mapIndex;
            while (s >= 0 && handles[s].Kind == handleType)
            {
                s--;
            }

            int e = mapIndex;
            while (e < handles.Length && handles[e].Kind == handleType)
            {
                e++;
            }

            start = s + 1;
            count = e - start;
            return true;
        }

        private MethodDefinition GetMethod(MethodDefinitionHandle handle)
        {
            return Get(handle, (reader, h) => reader.GetMethodDefinition((MethodDefinitionHandle)h));
        }

        private BlobHandle GetLocalSignature(StandaloneSignatureHandle handle)
        {
            return Get(handle, (reader, h) => reader.GetStandaloneSignature((StandaloneSignatureHandle)h).Signature);
        }

        private TEntity Get<TEntity>(Handle handle, Func<MetadataReader, Handle, TEntity> getter)
        {
            if (_aggregator != null)
            {
                var generationHandle = _aggregator.GetGenerationHandle(handle, out int generation);
                return getter(_readers[generation], generationHandle);
            }
            else
            {
                return getter(_reader, handle);
            }
        }

        private static readonly Guid s_CSharpGuid = new Guid("3f5162f8-07c6-11d3-9053-00c04fa302a1");
        private static readonly Guid s_visualBasicGuid = new Guid("3a12d0b8-c26c-11d0-b442-00a0244a1dd2");
        private static readonly Guid s_FSharpGuid = new Guid("ab4f38c9-b6e6-43ba-be3b-58080b2ccce3");
        private static readonly Guid s_sha1Guid = new Guid("ff1816ec-aa5e-4d10-87f7-6f4963833460");
        private static readonly Guid s_sha256Guid = new Guid("8829d00f-11b8-4213-878b-770e8597ac16");

        private static string GetLanguage(Guid guid)
        {
            if (guid == s_CSharpGuid) return "C#";
            if (guid == s_visualBasicGuid) return "Visual Basic";
            if (guid == s_FSharpGuid) return "F#";

            return "{" + guid + "}";
        }

        private static string GetHashAlgorithm(Guid guid)
        {
            if (guid == s_sha1Guid) return "SHA-1";
            if (guid == s_sha256Guid) return "SHA-256";

            return "{" + guid + "}";
        }

        // TODO: update Microsoft.CodeAnalysis.Debugging package and remove
        public static readonly Guid CompilationMetadataReferences = new Guid("7E4D4708-096E-4C5C-AEDA-CB10BA6A740D");
        public static readonly Guid CompilationOptions = new Guid("B5FEEC05-8CD0-4A83-96DA-466284BB4BD8");

        private string GetCustomDebugInformationKind(Guid guid)
        {
            if (guid == PortableCustomDebugInfoKinds.AsyncMethodSteppingInformationBlob) return "Async Method Stepping Information";
            if (guid == PortableCustomDebugInfoKinds.StateMachineHoistedLocalScopes) return "State Machine Hoisted Local Scopes";
            if (guid == PortableCustomDebugInfoKinds.DynamicLocalVariables) return "Dynamic Local Variables";
            if (guid == PortableCustomDebugInfoKinds.TupleElementNames) return "Tuple Element Names";
            if (guid == PortableCustomDebugInfoKinds.DefaultNamespace) return "Default Namespace";
            if (guid == PortableCustomDebugInfoKinds.EncLocalSlotMap) return "EnC Local Slot Map";
            if (guid == PortableCustomDebugInfoKinds.EncLambdaAndClosureMap) return "EnC Lambda and Closure Map";
            if (guid == PortableCustomDebugInfoKinds.EmbeddedSource) return "Embedded Source";
            if (guid == PortableCustomDebugInfoKinds.SourceLink) return "Source Link";
            if (guid == CompilationMetadataReferences) return "Compilation Metadata References";
            if (guid == CompilationOptions) return "Compilation Options";

            return "{" + guid + "}";
        }

        private string Language(Func<GuidHandle> getHandle) =>
            Literal(() => getHandle(), (r, h) => GetLanguage(r.GetGuid((GuidHandle)h)));

        private string HashAlgorithm(Func<GuidHandle> getHandle) =>
            Literal(() => getHandle(), (r, h) => GetHashAlgorithm(r.GetGuid((GuidHandle)h)));

        private string CustomDebugInformationKind(Func<GuidHandle> getHandle) =>
            Literal(() => getHandle(), (r, h) => GetCustomDebugInformationKind(r.GetGuid((GuidHandle)h)));

        private string DocumentName(Func<DocumentNameBlobHandle> getHandle) =>
            Literal(() => getHandle(), BlobKind.DocumentName, (r, h) => "'" + StringUtilities.EscapeNonPrintableCharacters(r.GetString((DocumentNameBlobHandle)h)) + "'");

        private string LiteralUtf8Blob(Func<BlobHandle> getHandle, BlobKind kind)
        {
            return Literal(getHandle, kind, (r, h) =>
            {
                var bytes = r.GetBlobBytes(h);
                return "'" + Encoding.UTF8.GetString(bytes, 0, bytes.Length) + "'";
            });
        }

        private string FieldSignature(Func<BlobHandle> getHandle) =>
            Literal(getHandle, BlobKind.FieldSignature, (r, h) => Signature(r, h, BlobKind.FieldSignature));

        private string MethodSignature(Func<BlobHandle> getHandle) =>
            Literal(getHandle, BlobKind.MethodSignature, (r, h) => Signature(r, h, BlobKind.MethodSignature));

        private string StandaloneSignature(Func<BlobHandle> getHandle) =>
            Literal(getHandle, BlobKind.StandAloneSignature, (r, h) => Signature(r, h, BlobKind.StandAloneSignature));

        private string MemberReferenceSignature(Func<BlobHandle> getHandle) =>
            Literal(getHandle, BlobKind.MemberRefSignature, (r, h) => Signature(r, h, BlobKind.MemberRefSignature));

        private string MethodSpecificationSignature(Func<BlobHandle> getHandle) =>
            Literal(getHandle, BlobKind.MethodSpec, (r, h) => Signature(r, h, BlobKind.MethodSpec));

        private string TypeSpecificationSignature(Func<BlobHandle> getHandle) =>
            Literal(getHandle, BlobKind.TypeSpec, (r, h) => Signature(r, h, BlobKind.TypeSpec));

        public string MethodSignature(BlobHandle signatureHandle) =>
            Literal(signatureHandle, (r, h) => Signature(r, (BlobHandle)h, BlobKind.MethodSignature));

        public string StandaloneSignature(BlobHandle signatureHandle) =>
            Literal(signatureHandle, (r, h) => Signature(r, (BlobHandle)h, BlobKind.StandAloneSignature));

        public string MemberReferenceSignature(BlobHandle signatureHandle) =>
            Literal(signatureHandle, (r, h) => Signature(r, (BlobHandle)h, BlobKind.MemberRefSignature));

        public string MethodSpecificationSignature(BlobHandle signatureHandle) =>
            Literal(signatureHandle, (r, h) => Signature(r, (BlobHandle)h, BlobKind.MethodSpec));

        public string TypeSpecificationSignature(BlobHandle signatureHandle) =>
            Literal(signatureHandle, (r, h) => Signature(r, (BlobHandle)h, BlobKind.TypeSpec));

        private string Signature(MetadataReader reader, BlobHandle signatureHandle, BlobKind kind)
        {
            try
            {
                var sigReader = reader.GetBlobReader(signatureHandle);
                var decoder = new SignatureDecoder<string, object>(_signatureVisualizer, reader, genericContext: null);
                switch (kind)
                {
                    case BlobKind.FieldSignature:
                        return decoder.DecodeFieldSignature(ref sigReader);

                    case BlobKind.MethodSignature:
                        return MethodSignature(decoder.DecodeMethodSignature(ref sigReader));

                    case BlobKind.StandAloneSignature:
                        return string.Join(", ", decoder.DecodeLocalSignature(ref sigReader));

                    case BlobKind.MemberRefSignature:
                        var header = sigReader.ReadSignatureHeader();
                        sigReader.Offset = 0;
                        switch (header.Kind)
                        {
                            case SignatureKind.Field:
                                return decoder.DecodeFieldSignature(ref sigReader);

                            case SignatureKind.Method:
                                return MethodSignature(decoder.DecodeMethodSignature(ref sigReader));
                        }

                        throw new BadImageFormatException();

                    case BlobKind.MethodSpec:
                        return string.Join(", ", decoder.DecodeMethodSpecificationSignature(ref sigReader));

                    case BlobKind.TypeSpec:
                        return decoder.DecodeType(ref sigReader, allowTypeSpecifications: false);

                    default:
                        throw ExceptionUtilities.UnexpectedValue(kind);
                }
            }
            catch (BadImageFormatException)
            {
                return $"<bad signature: {BitConverter.ToString(reader.GetBlobBytes(signatureHandle))}>";
            }
        }

        private static string MethodSignature(MethodSignature<string> signature)
        {
            var builder = new StringBuilder();
            builder.Append(signature.ReturnType);
            builder.Append(' ');
            builder.Append('(');

            for (int i = 0; i < signature.ParameterTypes.Length; i++)
            {
                if (i > 0)
                {
                    builder.Append(", ");

                    if (i == signature.RequiredParameterCount)
                    {
                        builder.Append("... ");
                    }
                }

                builder.Append(signature.ParameterTypes[i]);
            }

            builder.Append(')');
            return builder.ToString();
        }

        private string Literal(Func<BlobHandle> getHandle, BlobKind kind) =>
            Literal(getHandle, kind, (r, h) => BitConverter.ToString(r.GetBlobBytes(h)));

        private string Literal(Func<BlobHandle> getHandle, BlobKind kind, Func<MetadataReader, BlobHandle, string> getValue)
        {
            BlobHandle handle;
            try
            {
                handle = getHandle();
            }
            catch (BadImageFormatException)
            {
                return BadMetadataStr;
            }

            if (!handle.IsNil && kind != BlobKind.None)
            {
                _blobKinds[handle] = kind;
            }

            return Literal(handle, (r, h) => getValue(r, (BlobHandle)h));
        }

        private string Literal(Func<StringHandle> getHandle) =>
            Literal(() => getHandle(), (r, h) => "'" + StringUtilities.EscapeNonPrintableCharacters(r.GetString((StringHandle)h)) + "'");

        private string Literal(Func<NamespaceDefinitionHandle> getHandle) =>
            Literal(() => getHandle(), (r, h) => "'" + StringUtilities.EscapeNonPrintableCharacters(r.GetString((NamespaceDefinitionHandle)h)) + "'");

        private string Literal(Func<GuidHandle> getHandle) =>
            Literal(() => getHandle(), (r, h) => "{" + r.GetGuid((GuidHandle)h) + "}");

        private string Literal(Func<Handle> getHandle, Func<MetadataReader, Handle, string> getValue)
        {
            Handle handle;
            try
            {
                handle = getHandle();
            }
            catch (BadImageFormatException)
            {
                return BadMetadataStr;
            }

            return Literal(handle, getValue);
        }

        private string Literal(Handle handle, Func<MetadataReader, Handle, string> getValue)
        {
            if (handle.IsNil)
            {
                return "nil";
            }

            if (_aggregator != null)
            {
                Handle generationHandle = _aggregator.GetGenerationHandle(handle, out int generation);

                var generationReader = _readers[generation];
                string value = GetValueChecked(getValue, generationReader, generationHandle);
                int offset = generationReader.GetHeapOffset(handle);
                int generationOffset = generationReader.GetHeapOffset(generationHandle);

                if (NoHeapReferences)
                {
                    return value;
                }
                else if (offset == generationOffset)
                {
                    return $"{value} (#{offset:x})";
                }
                else
                {
                    return $"{value} (#{offset:x}/{generationOffset:x})";
                }
            }

            if (IsDelta)
            {
                // we can't resolve the literal without aggregate reader
                return $"#{_reader.GetHeapOffset(handle):x}";
            }

            int heapOffset = MetadataTokens.GetHeapOffset(handle);

            // virtual heap handles don't have offset:
            bool displayHeapOffset = !NoHeapReferences && heapOffset >= 0;

            return $"{GetValueChecked(getValue, _reader, handle):x}" + (displayHeapOffset ? $" (#{heapOffset:x})" : "");
        }

        private static string GetValueChecked(Func<MetadataReader, Handle, string> getValue, MetadataReader reader, Handle handle)
        {
            try
            {
                return getValue(reader, handle);
            }
            catch (BadImageFormatException)
            {
                return BadMetadataStr;
            }
        }

        private static bool TryGetValue<T>(Func<T> getValue, out T result)
        {
            try
            {
                result = getValue();
                return true;
            }
            catch (BadImageFormatException)
            {
                result = default;
                return false;
            }
        }

        private string ToString<TValue>(Func<TValue> getValue)
        {
            try
            {
                return getValue().ToString();
            }
            catch (BadImageFormatException)
            {
                return BadMetadataStr;
            }
        }

        private static string ToString<TValue>(Func<TValue> getValue, Func<TValue, string> valueToString)
        {
            try
            {
                return valueToString(getValue());
            }
            catch (BadImageFormatException)
            {
                return BadMetadataStr;
            }
        }

        private static string ToString<TValue, TArg>(Func<TValue> getValue, TArg arg, Func<TValue, TArg, string> valueToString)
        {
            try
            {
                return valueToString(getValue(), arg);
            }
            catch (BadImageFormatException)
            {
                return BadMetadataStr;
            }
        }

        private string Int32(Func<int> getValue)
            => ToString(getValue, value => value.ToString());

        private string Int32Hex(Func<int> getValue, int digits = 8)
            => ToString(getValue, value => "0x" + value.ToString("X" + digits));

        public string Token(Func<Handle> getHandle, bool displayTable = true)
            => ToString(getHandle, displayTable, Token);

        private string Token(Handle handle, bool displayTable = true)
        {
            string tokenStr = handle.IsNil ? "nil" : $"0x{_reader.GetToken(handle):x8}";

            if (displayTable && MetadataTokens.TryGetTableIndex(handle.Kind, out var table))
            {
                return $"{tokenStr} ({table})";
            }
            else
            {
                return tokenStr;
            }
        }

        private string RowId(Func<EntityHandle> getHandle)
            => ToString(getHandle, RowId);

        private string RowId(EntityHandle handle)
            => handle.IsNil ? "nil" : $"#{_reader.GetRowNumber(handle):x}";

        private string HeapOffset(Func<Handle> getHandle)
            => ToString(getHandle, HeapOffset);

        private string HeapOffset(Handle handle)
            => handle.IsNil ? "nil" : NoHeapReferences ? "" : $"#{_reader.GetHeapOffset(handle):x}";

        private static string EnumValue<TIntegral>(Func<object> getValue) where TIntegral : IEquatable<TIntegral>
        {
            object value;

            try
            {
                value = getValue();
            }
            catch (BadImageFormatException)
            {
                return BadMetadataStr;
            }

            TIntegral integralValue = (TIntegral)value;
            if (integralValue.Equals(default))
            {
                return "0";
            }

            return $"0x{integralValue:x8} ({value})";
        }

        // TODO (tomat): handle collections should implement IReadOnlyCollection<Handle>
        private string TokenRange<THandle>(IReadOnlyCollection<THandle> handles, Func<THandle, EntityHandle> conversion)
        {
            var genericHandles = handles.Select(conversion);

            if (handles.Count < 0)
            {
                return "<bad token range>";
            }

            return (handles.Count == 0) ? "nil" : Token(() => genericHandles.First(), displayTable: false) + "-" + Token(() => genericHandles.Last(), displayTable: false);
        }

        public string TokenList(IReadOnlyCollection<EntityHandle> handles, bool displayTable = false)
        {
            if (handles.Count == 0)
            {
                return "nil";
            }

            return string.Join(", ", handles.Select(h => Token(() => h, displayTable)));
        }

        private string Version(Func<Version> getVersion)
            => ToString(getVersion, version => version.Major + "." + version.Minor + "." + version.Build + "." + version.Revision);

        private string FormatAwaits(BlobHandle handle)
        {
            var sb = new StringBuilder();
            var blobReader = _reader.GetBlobReader(handle);

            while (blobReader.RemainingBytes > 0)
            {
                if (blobReader.Offset > 0)
                {
                    sb.Append(", ");
                }

                int value;
                sb.Append("(");
                sb.Append(blobReader.TryReadCompressedInteger(out value) ? value.ToString() : "?");
                sb.Append(", ");
                sb.Append(blobReader.TryReadCompressedInteger(out value) ? value.ToString() : "?");
                sb.Append(", ");
                sb.Append(blobReader.TryReadCompressedInteger(out value) ? Token(() => MetadataTokens.MethodDefinitionHandle(value)) : "?");
                sb.Append(')');
            }

            return sb.ToString();
        }

        private string FormatImports(ImportScope scope)
        {
            if (scope.ImportsBlob.IsNil)
            {
                return "nil";
            }

            var sb = new StringBuilder();

            foreach (var import in scope.GetImports())
            {
                if (sb.Length > 0)
                {
                    sb.Append(", ");
                }

                switch (import.Kind)
                {
                    case ImportDefinitionKind.ImportNamespace:
                        sb.AppendFormat("{0}", LiteralUtf8Blob(() => import.TargetNamespace, BlobKind.ImportNamespace));
                        break;

                    case ImportDefinitionKind.ImportAssemblyNamespace:
                        sb.AppendFormat("{0}::{1}",
                            Token(() => import.TargetAssembly),
                            LiteralUtf8Blob(() => import.TargetNamespace, BlobKind.ImportNamespace));
                        break;

                    case ImportDefinitionKind.ImportType:
                        sb.AppendFormat("{0}", Token(() => import.TargetType));
                        break;

                    case ImportDefinitionKind.ImportXmlNamespace:
                        sb.AppendFormat("<{0} = {1}>",
                            LiteralUtf8Blob(() => import.Alias, BlobKind.ImportAlias),
                            LiteralUtf8Blob(() => import.TargetNamespace, BlobKind.ImportNamespace));
                        break;

                    case ImportDefinitionKind.ImportAssemblyReferenceAlias:
                        sb.AppendFormat("Extern Alias {0}",
                            LiteralUtf8Blob(() => import.Alias, BlobKind.ImportAlias));
                        break;

                    case ImportDefinitionKind.AliasAssemblyReference:
                        sb.AppendFormat("{0} = {1}",
                            LiteralUtf8Blob(() => import.Alias, BlobKind.ImportAlias),
                            Token(() => import.TargetAssembly));
                        break;

                    case ImportDefinitionKind.AliasNamespace:
                        sb.AppendFormat("{0} = {1}",
                            LiteralUtf8Blob(() => import.Alias, BlobKind.ImportAlias),
                            LiteralUtf8Blob(() => import.TargetNamespace, BlobKind.ImportNamespace));
                        break;

                    case ImportDefinitionKind.AliasAssemblyNamespace:
                        sb.AppendFormat("{0} = {1}::{2}",
                            LiteralUtf8Blob(() => import.Alias, BlobKind.ImportAlias),
                            Token(() => import.TargetAssembly),
                            LiteralUtf8Blob(() => import.TargetNamespace, BlobKind.ImportNamespace));
                        break;

                    case ImportDefinitionKind.AliasType:
                        sb.AppendFormat("{0} = {1}",
                            LiteralUtf8Blob(() => import.Alias, BlobKind.ImportAlias),
                            Token(() => import.TargetType));
                        break;
                }
            }

            return sb.ToString();
        }

        private string SequencePoint(SequencePoint sequencePoint, bool includeDocument = true)
        {
            string range = sequencePoint.IsHidden ?
                "<hidden>" :
                $"({sequencePoint.StartLine}, {sequencePoint.StartColumn}) - ({sequencePoint.EndLine}, {sequencePoint.EndColumn})" +
                    (includeDocument ? $" [{RowId(() => sequencePoint.Document)}]" : "");

            return $"IL_{sequencePoint.Offset:X4}: " + range;
        }

        public void VisualizeHeaders()
        {
            _reader = _readers[0];

            _writer.WriteLine($"MetadataVersion: {_reader.MetadataVersion}");

            if (_reader.DebugMetadataHeader != null)
            {
                _writer.WriteLine("Id: " + BitConverter.ToString(_reader.DebugMetadataHeader.Id.ToArray()));

                if (!_reader.DebugMetadataHeader.EntryPoint.IsNil)
                {
                    _writer.WriteLine($"EntryPoint: {Token(() => _reader.DebugMetadataHeader.EntryPoint)}");
                }
            }

            _writer.WriteLine();
        }

        private void WriteModule()
        {
            if (_reader.DebugMetadataHeader != null)
            {
                return;
            }

            var def = _reader.GetModuleDefinition();

            var table = new TableBuilder(
                "Module (0x00):",
                "Gen",
                "Name",
                "Mvid",
                "EncId",
                "EncBaseId"
            );

            table.AddRow(
                ToString(() => def.Generation),
                Literal(() => def.Name),
                Literal(() => def.Mvid),
                Literal(() => def.GenerationId),
                Literal(() => def.BaseGenerationId));

            WriteTable(table);
        }

        private void WriteTypeRef()
        {
            var table = new TableBuilder(
                "TypeRef (0x01):",
                "Scope",
                "Name",
                "Namespace"
            );

            foreach (var handle in _reader.TypeReferences)
            {
                var entry = _reader.GetTypeReference(handle);

                table.AddRow(
                    Token(() => entry.ResolutionScope),
                    Literal(() => entry.Name),
                    Literal(() => entry.Namespace)
                );
            }

            WriteTable(table);
        }

        private void WriteTypeDef()
        {
            var table = new TableBuilder(
                "TypeDef (0x02):",
                "Name",
                "Namespace",
                "EnclosingType",
                "BaseType",
                "Interfaces",
                "Fields",
                "Methods",
                "Attributes",
                "ClassSize",
                "PackingSize"
            );

            foreach (var handle in _reader.TypeDefinitions)
            {
                var entry = _reader.GetTypeDefinition(handle);

                var layout = entry.GetLayout();

                // TODO: Visualize InterfaceImplementations
                var implementedInterfaces = entry.GetInterfaceImplementations().Select(h => _reader.GetInterfaceImplementation(h).Interface).ToArray();

                table.AddRow(
                    Literal(() => entry.Name),
                    Literal(() => entry.Namespace),
                    Token(() => entry.GetDeclaringType()),
                    Token(() => entry.BaseType),
                    TokenList(implementedInterfaces),
                    TokenRange(entry.GetFields(), h => h),
                    TokenRange(entry.GetMethods(), h => h),
                    EnumValue<int>(() => entry.Attributes),
                    !layout.IsDefault ? layout.Size.ToString() : "n/a",
                    !layout.IsDefault ? layout.PackingSize.ToString() : "n/a"
                );
            }

            WriteTable(table);
        }

        private void WriteField()
        {
            var table = new TableBuilder(
                "Field (0x04):",
                "Name",
                "Signature",
                "Attributes",
                "Marshalling",
                "Offset",
                "RVA"
            );

            foreach (var handle in _reader.FieldDefinitions)
            {
                var entry = _reader.GetFieldDefinition(handle);

                table.AddRow(
                    Literal(() => entry.Name),
                    FieldSignature(() => entry.Signature),
                    EnumValue<int>(() => entry.Attributes),
                    Literal(() => entry.GetMarshallingDescriptor(), BlobKind.Marshalling),
                    ToString(() =>
                    {
                        int offset = entry.GetOffset();
                        return offset >= 0 ? offset.ToString() : "n/a";
                    }),
                    ToString(() => entry.GetRelativeVirtualAddress())
                );
            }

            WriteTable(table);
        }

        private void WriteMethod()
        {
            var table = new TableBuilder(
                "Method (0x06, 0x1C):",
                "Name",
                "Signature",
                "RVA",
                "Parameters",
                "GenericParameters",
                "Attributes",
                "ImplAttributes",
                "ImportAttributes",
                "ImportName",
                "ImportModule"
            );

            foreach (var handle in _reader.MethodDefinitions)
            {
                var entry = _reader.GetMethodDefinition(handle);
                var import = entry.GetImport();

                table.AddRow(
                    Literal(() => entry.Name),
                    MethodSignature(() => entry.Signature),
                    Int32Hex(() => entry.RelativeVirtualAddress),
                    TokenRange(entry.GetParameters(), h => h),
                    TokenRange(entry.GetGenericParameters(), h => h),
                    EnumValue<int>(() => entry.Attributes),    // TODO: we need better visualizer than the default enum
                    EnumValue<int>(() => entry.ImplAttributes),
                    EnumValue<short>(() => import.Attributes),
                    Literal(() => import.Name),
                    Token(() => import.Module)
                );
            }

            WriteTable(table);
        }

        private void WriteParam()
        {
            var table = new TableBuilder(
                "Param (0x08):",
                "Name",
                "Seq#",
                "Attributes",
                "Marshalling"
            );

            for (int i = 1, count = _reader.GetTableRowCount(TableIndex.Param); i <= count; i++)
            {
                var entry = _reader.GetParameter(MetadataTokens.ParameterHandle(i));

                table.AddRow(
                    Literal(() => entry.Name),
                    ToString(() => entry.SequenceNumber),
                    EnumValue<int>(() => entry.Attributes),
                    Literal(() => entry.GetMarshallingDescriptor(), BlobKind.Marshalling)
                );
            }

            WriteTable(table);
        }

        private void WriteMemberRef()
        {
            var table = new TableBuilder(
                "MemberRef (0x0a):",
                "Parent",
                "Name",
                "Signature"
            );

            foreach (var handle in _reader.MemberReferences)
            {
                var entry = _reader.GetMemberReference(handle);

                table.AddRow(
                    Token(() => entry.Parent),
                    Literal(() => entry.Name),
                    MemberReferenceSignature(() => entry.Signature)
                );
            }

            WriteTable(table);
        }

        private void WriteConstant()
        {
            var table = new TableBuilder(
                "Constant (0x0b):",
                "Parent",
                "Type",
                "Value"
            );

            for (int i = 1, count = _reader.GetTableRowCount(TableIndex.Constant); i <= count; i++)
            {
                var entry = _reader.GetConstant(MetadataTokens.ConstantHandle(i));

                table.AddRow(
                    Token(() => entry.Parent),
                    EnumValue<byte>(() => entry.TypeCode),
                    Literal(() => entry.Value, BlobKind.ConstantValue)
                );
            }

            WriteTable(table);
        }

        private void WriteCustomAttribute()
        {
            var table = new TableBuilder(
                "CustomAttribute (0x0c):",
                "Parent",
                "Constructor",
                "Value"
            );

            foreach (var handle in _reader.CustomAttributes)
            {
                var entry = _reader.GetCustomAttribute(handle);

                table.AddRow(
                    Token(() => entry.Parent),
                    Token(() => entry.Constructor),
                    Literal(() => entry.Value, BlobKind.CustomAttribute)
                );
            }

            WriteTable(table);
        }

        private void WriteDeclSecurity()
        {
            var table = new TableBuilder(
                "DeclSecurity (0x0e):",
                "Parent",
                "PermissionSet",
                "Action"
            );

            foreach (var handle in _reader.DeclarativeSecurityAttributes)
            {
                var entry = _reader.GetDeclarativeSecurityAttribute(handle);

                table.AddRow(
                    Token(() => entry.Parent),
                    Literal(() => entry.PermissionSet, BlobKind.PermissionSet),
                    EnumValue<short>(() => entry.Action)
                );
            }

            WriteTable(table);
        }

        private void WriteStandAloneSig()
        {
            var table = new TableBuilder(
                "StandAloneSig (0x11):",
                "Signature"
            );

            for (int i = 1, count = _reader.GetTableRowCount(TableIndex.StandAloneSig); i <= count; i++)
            {
                var entry = _reader.GetStandaloneSignature(MetadataTokens.StandaloneSignatureHandle(i));
                table.AddRow(StandaloneSignature(() => entry.Signature));
            }

            WriteTable(table);
        }

        private void WriteEvent()
        {
            var table = new TableBuilder(
                "Event (0x12, 0x14, 0x18):",
                "Name",
                "Add",
                "Remove",
                "Fire",
                "Attributes"
            );

            foreach (var handle in _reader.EventDefinitions)
            {
                var entry = _reader.GetEventDefinition(handle);
                var accessors = entry.GetAccessors();

                table.AddRow(
                    Literal(() => entry.Name),
                    Token(() => accessors.Adder),
                    Token(() => accessors.Remover),
                    Token(() => accessors.Raiser),
                    EnumValue<int>(() => entry.Attributes)
                );
            }

            WriteTable(table);
        }

        private void WriteProperty()
        {
            var table = new TableBuilder(
                "Property (0x15, 0x17, 0x18):",
                "Name",
                "Get",
                "Set",
                "Attributes"
            );

            foreach (var handle in _reader.PropertyDefinitions)
            {
                var entry = _reader.GetPropertyDefinition(handle);
                var accessors = entry.GetAccessors();

                table.AddRow(
                    Literal(() => entry.Name),
                    Token(() => accessors.Getter),
                    Token(() => accessors.Setter),
                    EnumValue<int>(() => entry.Attributes)
                );
            }

            WriteTable(table);
        }

        private void WriteMethodImpl()
        {
            var table = new TableBuilder(
                "MethodImpl (0x19):",
                "Type",
                "Body",
                "Declaration"
            );

            for (int i = 1, count = _reader.GetTableRowCount(TableIndex.MethodImpl); i <= count; i++)
            {
                var entry = _reader.GetMethodImplementation(MetadataTokens.MethodImplementationHandle(i));

                table.AddRow(
                    Token(() => entry.Type),
                    Token(() => entry.MethodBody),
                    Token(() => entry.MethodDeclaration)
                );
            }

            WriteTable(table);
        }

        private void WriteModuleRef()
        {
            var table = new TableBuilder(
                "ModuleRef (0x1a):",
                "Name"
            );

            for (int i = 1, count = _reader.GetTableRowCount(TableIndex.ModuleRef); i <= count; i++)
            {
                var entry = _reader.GetModuleReference(MetadataTokens.ModuleReferenceHandle(i));
                table.AddRow(Literal(() => entry.Name));
            }

            WriteTable(table);
        }

        private void WriteTypeSpec()
        {
            var table = new TableBuilder(
                "TypeSpec (0x1b):",
                "Name");

            for (int i = 1, count = _reader.GetTableRowCount(TableIndex.TypeSpec); i <= count; i++)
            {
                var entry = _reader.GetTypeSpecification(MetadataTokens.TypeSpecificationHandle(i));
                table.AddRow(TypeSpecificationSignature(() => entry.Signature));
            }

            WriteTable(table);
        }

        private void WriteEnCLog()
        {
            var table = new TableBuilder(
                "EnC Log (0x1e):",
                "Entity",
                "Operation");

            foreach (var entry in _reader.GetEditAndContinueLogEntries())
            {
                table.AddRow(
                    Token(() => entry.Handle),
                    EnumValue<int>(() => entry.Operation));
            }

            WriteTable(table);
        }

        private void WriteEnCMap()
        {
            TableBuilder table;
            if (_aggregator != null)
            {
                table = new TableBuilder("EnC Map (0x1f):", "Entity", "Gen", "Row", "Edit");
            }
            else
            {
                table = new TableBuilder("EnC Map (0x1f):", "Entity");
            }

            foreach (var entry in _reader.GetEditAndContinueMapEntries())
            {
                if (_aggregator != null)
                {
                    int generation;
                    EntityHandle primary = (EntityHandle)_aggregator.GetGenerationHandle(entry, out generation);
                    bool isUpdate = _readers[generation] != _reader;

                    var primaryModule = _readers[generation].GetModuleDefinition();

                    table.AddRow(
                        Token(() => entry),
                        ToString(() => primaryModule.Generation),
                        "0x" + MetadataTokens.GetRowNumber(primary).ToString("x6"),
                        isUpdate ? "update" : "add");
                }
                else
                {
                    table.AddRow(Token(() => entry));
                }
            }

            WriteTable(table);
        }

        private void WriteAssembly()
        {
            if (!_reader.IsAssembly)
            {
                return;
            }

            var table = new TableBuilder(
                "Assembly (0x20):",
                "Name",
                "Version",
                "Culture",
                "PublicKey",
                "Flags",
                "HashAlgorithm"
            );

            var entry = _reader.GetAssemblyDefinition();

            table.AddRow(
                Literal(() => entry.Name),
                Version(() => entry.Version),
                Literal(() => entry.Culture),
                Literal(() => entry.PublicKey, BlobKind.Key),
                EnumValue<int>(() => entry.Flags),
                EnumValue<int>(() => entry.HashAlgorithm)
            );

            WriteTable(table);
        }

        private void WriteAssemblyRef()
        {
            var table = new TableBuilder(
                "AssemblyRef (0x23):",
                "Name",
                "Version",
                "Culture",
                "PublicKeyOrToken",
                "Flags"
            );

            foreach (var handle in _reader.AssemblyReferences)
            {
                var entry = _reader.GetAssemblyReference(handle);

                table.AddRow(
                    Literal(() => entry.Name),
                    Version(() => entry.Version),
                    Literal(() => entry.Culture),
                    Literal(() => entry.PublicKeyOrToken, BlobKind.Key),
                    EnumValue<int>(() => entry.Flags)
                );
            }

            WriteTable(table);
        }

        private void WriteFile()
        {
            var table = new TableBuilder(
                "File (0x26):",
                "Name",
                "Metadata",
                "HashValue"
            );

            foreach (var handle in _reader.AssemblyFiles)
            {
                var entry = _reader.GetAssemblyFile(handle);

                table.AddRow(
                    Literal(() => entry.Name),
                    entry.ContainsMetadata ? "Yes" : "No",
                    Literal(() => entry.HashValue, BlobKind.FileHash)
                );
            }

            WriteTable(table);
        }

        private void WriteExportedType()
        {
            var table = new TableBuilder(
                "ExportedType (0x27):",
                "Name",
                "Namespace",
                "Attributes",
                "Implementation",
                "TypeDefinitionId"
            );

            const TypeAttributes TypeForwarder = (TypeAttributes)0x00200000;

            foreach (var handle in _reader.ExportedTypes)
            {
                var entry = _reader.GetExportedType(handle);

                table.AddRow(
                    Literal(() => entry.Name),
                    Literal(() => entry.Namespace),
                    ToString(() => ((entry.Attributes & TypeForwarder) == TypeForwarder ? "TypeForwarder, " : "") + (entry.Attributes & ~TypeForwarder).ToString()),
                    Token(() => entry.Implementation),
                    Int32Hex(() => entry.GetTypeDefinitionId())
                );
            }

            WriteTable(table);
        }

        private void WriteManifestResource()
        {
            var table = new TableBuilder(
                "ManifestResource (0x28):",
                "Name",
                "Attributes",
                "Offset",
                "Implementation"
            );

            foreach (var handle in _reader.ManifestResources)
            {
                var entry = _reader.GetManifestResource(handle);

                table.AddRow(
                    Literal(() => entry.Name),
                    ToString(() => entry.Attributes),
                    ToString(() => entry.Offset),
                    Token(() => entry.Implementation)
                );
            }

            WriteTable(table);
        }

        private void WriteGenericParam()
        {
            var table = new TableBuilder(
                "GenericParam (0x2a):",
                "Name",
                "Seq#",
                "Attributes",
                "Parent",
                "TypeConstraints"
            );

            for (int i = 1, count = _reader.GetTableRowCount(TableIndex.GenericParam); i <= count; i++)
            {
                var entry = _reader.GetGenericParameter(MetadataTokens.GenericParameterHandle(i));

                table.AddRow(
                    Literal(() => entry.Name),
                    ToString(() => entry.Index),
                    EnumValue<int>(() => entry.Attributes),
                    Token(() => entry.Parent),
                    TokenRange(entry.GetConstraints(), h => h)
                );
            }

            WriteTable(table);
        }

        private void WriteMethodSpec()
        {
            var table = new TableBuilder(
                "MethodSpec (0x2b):",
                "Method",
                "Signature"
            );

            for (int i = 1, count = _reader.GetTableRowCount(TableIndex.MethodSpec); i <= count; i++)
            {
                var entry = _reader.GetMethodSpecification(MetadataTokens.MethodSpecificationHandle(i));

                table.AddRow(
                    Token(() => entry.Method),
                    MethodSpecificationSignature(() => entry.Signature)
                );
            }

            WriteTable(table);
        }

        private void WriteGenericParamConstraint()
        {
            var table = new TableBuilder(
                "GenericParamConstraint (0x2c):",
                "Parent",
                "Type"
            );

            for (int i = 1, count = _reader.GetTableRowCount(TableIndex.GenericParamConstraint); i <= count; i++)
            {
                var entry = _reader.GetGenericParameterConstraint(MetadataTokens.GenericParameterConstraintHandle(i));

                table.AddRow(
                    Token(() => entry.Parameter),
                    Token(() => entry.Type)
                );
            }

            WriteTable(table);
        }

        private void WriteUserStrings()
        {
            int size = _reader.GetHeapSize(HeapIndex.UserString);
            if (size == 0)
            {
                return;
            }

            // TODO: the heap is aligned, don't display the trailing empty strings
            _writer.WriteLine($"#US (size = {size}):");
            var handle = MetadataTokens.UserStringHandle(0);
            do
            {
                string value = StringUtilities.EscapeNonPrintableCharacters(_reader.GetUserString(handle));
                _writer.WriteLine($"  {_reader.GetHeapOffset(handle):x}: '{value}'");
                handle = _reader.GetNextHandle(handle);
            }
            while (!handle.IsNil);

            _writer.WriteLine();
        }

        private void WriteStrings()
        {
            int size = _reader.GetHeapSize(HeapIndex.String);
            if (size == 0)
            {
                return;
            }

            _writer.WriteLine($"#String (size = {size}):");
            var handle = MetadataTokens.StringHandle(0);
            do
            {
                string value = StringUtilities.EscapeNonPrintableCharacters(_reader.GetString(handle));
                _writer.WriteLine($"  {_reader.GetHeapOffset(handle):x}: '{value}'");
                handle = _reader.GetNextHandle(handle);
            }
            while (!handle.IsNil);

            _writer.WriteLine();
        }

        private void WriteBlobs()
        {
            int size = _reader.GetHeapSize(HeapIndex.Blob);
            if (size == 0)
            {
                return;
            }

            int[] sizePerKind = new int[(int)BlobKind.Count];

            _writer.WriteLine($"#Blob (size = {size}):");
            var handle = MetadataTokens.BlobHandle(0);
            do
            {
                byte[] value = _reader.GetBlobBytes(handle);

                BlobKind kind;
                string kindString;
                if (_blobKinds.TryGetValue(handle, out kind))
                {
                    kindString = " (" + kind + ")";

                    // ignoring the compressed blob size:
                    sizePerKind[(int)kind] += value.Length;
                }
                else
                {
                    kindString = "";
                }

                int displayLength = (_options & MetadataVisualizerOptions.ShortenBlobs) != 0 ? Math.Min(4, value.Length) : value.Length;
                string valueString = BitConverter.ToString(value, 0, displayLength) + (displayLength < value.Length ? "-..." : null);

                _writer.WriteLine($"  {_reader.GetHeapOffset(handle):x}{kindString}: {valueString}");
                handle = _reader.GetNextHandle(handle);
            }
            while (!handle.IsNil);

            _writer.WriteLine();
            _writer.WriteLine("Sizes:");

            for (int i = 0; i < sizePerKind.Length; i++)
            {
                if (sizePerKind[i] > 0)
                {
                    _writer.WriteLine($"  {(BlobKind)i}: {(decimal)sizePerKind[i]} bytes");
                }
            }

            // don't calculate statistics for EnC delta, it's not interesting
            if (_aggregator == null)
            {
                _writer.WriteLine();
                _writer.WriteLine("CustomAttribute sizes by constructor:");
                try
                {
                    foreach (var grouping in from caHandle in _reader.CustomAttributes
                                             let ca = _reader.GetCustomAttribute(caHandle)
                                             group ca.Constructor by ca.Value into values   // blob -> { ctor1, ctor2, ... }
                                             group values.Key by values.First() into g      // ctor1 -> { blob1, ... }
                                             select new { Ctor = g.Key, Size = g.Sum(ca => _reader.GetBlobReader(ca).Length) } into ctorAndSize
                                             orderby ctorAndSize.Size descending
                                             select ctorAndSize)
                    {
                        string typeStr = null;
                        switch (grouping.Ctor.Kind)
                        {
                            case HandleKind.MemberReference:
                                var memberRef = _reader.GetMemberReference((MemberReferenceHandle)grouping.Ctor);

                                switch (memberRef.Parent.Kind)
                                {
                                    case HandleKind.TypeReference:
                                        var typeRef = _reader.GetTypeReference((TypeReferenceHandle)memberRef.Parent);
                                        typeStr = typeRef.Namespace.IsNil ? _reader.GetString(typeRef.Name) : _reader.GetString(typeRef.Namespace) + "." + _reader.GetString(typeRef.Name);
                                        break;

                                    case HandleKind.TypeDefinition:
                                        var typeDef = _reader.GetTypeDefinition((TypeDefinitionHandle)memberRef.Parent);
                                        typeStr = typeDef.Namespace.IsNil ? _reader.GetString(typeDef.Name) : _reader.GetString(typeDef.Namespace) + "." + _reader.GetString(typeDef.Name);
                                        break;

                                    case HandleKind.MethodDefinition:
                                    case HandleKind.ModuleReference:
                                    case HandleKind.TypeSpecification:
                                        break;
                                }

                                break;

                            case HandleKind.MethodDefinition:
                                // TODO
                                break;
                        }


                        // grouping.Key
                        _writer.WriteLine($"  {typeStr ?? Token(() => grouping.Ctor)}: {grouping.Size} bytes");
                    }
                }
                catch (BadImageFormatException)
                {
                    _writer.WriteLine(BadMetadataStr);
                }

                _writer.WriteLine();
            }
        }

        private void WriteGuids()
        {
            int size = _reader.GetHeapSize(HeapIndex.Guid);
            if (size == 0)
            {
                return;
            }

            _writer.WriteLine(string.Format("#Guid (size = {0}):", size));
            int i = 1;
            while (i <= size / 16)
            {
                string value = _reader.GetGuid(MetadataTokens.GuidHandle(i)).ToString();
                _writer.WriteLine("  {0:x}: {{{1}}}", i, value);
                i++;
            }

            _writer.WriteLine();
        }

        public void WriteDocument()
        {
            var table = new TableBuilder(
                MakeTableName(TableIndex.Document),
                "Name",
                "Language",
                "HashAlgorithm",
                "Hash"
            );

            foreach (var handle in _reader.Documents)
            {
                var entry = _reader.GetDocument(handle);

                table.AddRow(
                    DocumentName(() => entry.Name),
                    Language(() => entry.Language),
                    HashAlgorithm(() => entry.HashAlgorithm),
                    Literal(() => entry.Hash, BlobKind.DocumentHash)
               );
            }

            WriteTable(table);
        }

        public void WriteMethodDebugInformation()
        {
            if (_reader.MethodDebugInformation.Count == 0)
            {
                return;
            }

            var table = new TableBuilder(
                MakeTableName(TableIndex.MethodDebugInformation),
                "IL"
            );

            var detailsBuilder = new StringBuilder();

            foreach (var handle in _reader.MethodDebugInformation)
            {
                if (handle.IsNil)
                {
                    continue;
                }

                var entry = _reader.GetMethodDebugInformation(handle);

                bool hasSingleDocument = false;
                bool hasSequencePoints = false;
                try
                {
                    hasSingleDocument = !entry.Document.IsNil;
                    hasSequencePoints = !entry.SequencePointsBlob.IsNil;
                }
                catch (BadImageFormatException)
                {
                    hasSingleDocument = hasSequencePoints = false;
                }

                string details;

                if (hasSequencePoints)
                {
                    _blobKinds[entry.SequencePointsBlob] = BlobKind.SequencePoints;

                    detailsBuilder.Clear();
                    detailsBuilder.AppendLine("{");

                    bool addLineBreak = false;

                    if (!TryGetValue(() => entry.GetStateMachineKickoffMethod(), out var kickoffMethod) || !kickoffMethod.IsNil)
                    {
                        detailsBuilder.AppendLine($"  Kickoff Method: {(kickoffMethod.IsNil ? BadMetadataStr : Token(kickoffMethod))}");
                        addLineBreak = true;
                    }

                    if (!TryGetValue(() => entry.LocalSignature, out var localSignature) || !localSignature.IsNil)
                    {
                        detailsBuilder.AppendLine($"  Locals: {(localSignature.IsNil ? BadMetadataStr : Token(localSignature))}");
                        addLineBreak = true;
                    }

                    if (hasSingleDocument)
                    {
                        detailsBuilder.AppendLine($"  Document: {RowId(() => entry.Document)}");
                        addLineBreak = true;
                    }

                    if (addLineBreak)
                    {
                        detailsBuilder.AppendLine();
                    }

                    try
                    {
                        foreach (var sequencePoint in entry.GetSequencePoints())
                        {
                            detailsBuilder.Append("  ");
                            detailsBuilder.AppendLine(SequencePoint(sequencePoint, includeDocument: !hasSingleDocument));
                        }
                    }
                    catch (BadImageFormatException)
                    {
                        detailsBuilder.AppendLine("  " + BadMetadataStr);
                    }

                    detailsBuilder.AppendLine("}");
                    details = detailsBuilder.ToString();
                }
                else
                {
                    details = null;
                }

                table.AddRowWithDetails(new[] { HeapOffset(() => entry.SequencePointsBlob) }, details);
            }

            WriteTable(table);
        }

        public void WriteLocalScope()
        {
            var table = new TableBuilder(
                MakeTableName(TableIndex.LocalScope),
                "Method",
                "ImportScope",
                "Variables",
                "Constants",
                "StartOffset",
                "Length"
            );

            foreach (var handle in _reader.LocalScopes)
            {
                var entry = _reader.GetLocalScope(handle);

                table.AddRow(
                    Token(() => entry.Method),
                    Token(() => entry.ImportScope),
                    TokenRange(entry.GetLocalVariables(), h => h),
                    TokenRange(entry.GetLocalConstants(), h => h),
                    Int32Hex(() => entry.StartOffset, digits: 4),
                    Int32(() => entry.Length)
               );
            }

            WriteTable(table);
        }

        public void WriteLocalVariable()
        {
            var table = new TableBuilder(
                MakeTableName(TableIndex.LocalVariable),
                "Name",
                "Index",
                "Attributes"
            );

            foreach (var handle in _reader.LocalVariables)
            {
                var entry = _reader.GetLocalVariable(handle);

                table.AddRow(
                    Literal(() => entry.Name),
                    Int32(() => entry.Index),
                    EnumValue<int>(() => entry.Attributes)
               );
            }

            WriteTable(table);
        }

        public void WriteLocalConstant()
        {
            var table = new TableBuilder(
                MakeTableName(TableIndex.LocalConstant),
                "Name",
                "Signature"
            );

            foreach (var handle in _reader.LocalConstants)
            {
                var entry = _reader.GetLocalConstant(handle);

                table.AddRow(
                    Literal(() => entry.Name),
                    Literal(() => entry.Signature, BlobKind.LocalConstantSignature, (r, h) => FormatLocalConstant(r, (BlobHandle)h))
               );
            }

            WriteTable(table);
        }

        private SignatureTypeCode ReadConstantTypeCode(ref BlobReader sigReader, List<string> modifiers)
        {
            while (true)
            {
                var s = sigReader.ReadSignatureTypeCode();
                if (s == SignatureTypeCode.OptionalModifier || s == SignatureTypeCode.RequiredModifier)
                {
                    var type = sigReader.ReadTypeHandle();
                    modifiers.Add((s == SignatureTypeCode.RequiredModifier ? "modreq" : "modopt") + "(" + Token(() => type) + ")");
                }
                else
                {
                    return s;
                }
            }
        }

        private string FormatLocalConstant(MetadataReader reader, BlobHandle signature)
        {
            var sigReader = reader.GetBlobReader(signature);

            var modifiers = new List<string>();

            SignatureTypeCode typeCode = ReadConstantTypeCode(ref sigReader, modifiers);

            Handle typeHandle = default;
            object value;
            if (IsPrimitiveType(typeCode))
            {
                if (typeCode == SignatureTypeCode.String)
                {
                    if (sigReader.RemainingBytes == 1)
                    {
                        value = (sigReader.ReadByte() == 0xff) ? "null" : BadMetadataStr;
                    }
                    else if (sigReader.RemainingBytes % 2 != 0)
                    {
                        value = BadMetadataStr;
                    }
                    else
                    {
                        value = "'" + StringUtilities.EscapeNonPrintableCharacters(sigReader.ReadUTF16(sigReader.RemainingBytes)) + "'";
                    }
                }
                else
                {
                    object rawValue = sigReader.ReadConstant((ConstantTypeCode)typeCode);
                    if (rawValue is char c)
                    {
                        value = "'" + StringUtilities.EscapeNonPrintableCharacters(c.ToString()) + "'";
                    }
                    else
                    {
                        value = string.Format(CultureInfo.InvariantCulture, "{0}", rawValue);
                    }
                }

                if (sigReader.RemainingBytes > 0)
                {
                    typeHandle = sigReader.ReadTypeHandle();
                }
            }
            else if (typeCode == SignatureTypeCode.TypeHandle)
            {
                typeHandle = sigReader.ReadTypeHandle();
                value = (sigReader.RemainingBytes > 0) ? BitConverter.ToString(sigReader.ReadBytes(sigReader.RemainingBytes)) : "default";
            }
            else
            {
                value = (typeCode == SignatureTypeCode.Object) ? "null" : $"<bad type code: {typeCode}>";
            }

            return string.Format("{0} [{1}{2}]",
                value,
                 string.Join(" ", modifiers),
                typeHandle.IsNil ? typeCode.ToString() : Token(() => typeHandle));
        }

        private static bool IsPrimitiveType(SignatureTypeCode typeCode)
        {
            switch (typeCode)
            {
                case SignatureTypeCode.Boolean:
                case SignatureTypeCode.Char:
                case SignatureTypeCode.SByte:
                case SignatureTypeCode.Byte:
                case SignatureTypeCode.Int16:
                case SignatureTypeCode.UInt16:
                case SignatureTypeCode.Int32:
                case SignatureTypeCode.UInt32:
                case SignatureTypeCode.Int64:
                case SignatureTypeCode.UInt64:
                case SignatureTypeCode.Single:
                case SignatureTypeCode.Double:
                case SignatureTypeCode.String:
                    return true;

                default:
                    return false;
            }
        }

        public void WriteImportScope()
        {
            var table = new TableBuilder(
                MakeTableName(TableIndex.ImportScope),
                "Parent",
                "Imports"
            );

            foreach (var handle in _reader.ImportScopes)
            {
                var entry = _reader.GetImportScope(handle);

                _blobKinds[entry.ImportsBlob] = BlobKind.Imports;

                table.AddRow(
                    Token(() => entry.Parent),
                    FormatImports(entry)
               );
            }

            WriteTable(table);
        }

        public void WriteCustomDebugInformation()
        {
            const int BlobSizeLimit = 32;

            var table = new TableBuilder(
                MakeTableName(TableIndex.CustomDebugInformation),
                "Parent",
                "Kind",
                "Value"
            );

            foreach (var handle in _reader.CustomDebugInformation)
            {
                var entry = _reader.GetCustomDebugInformation(handle);

                table.AddRowWithDetails(
                    fields: new[]
                    {
                        Token(() => entry.Parent),
                        CustomDebugInformationKind(() => entry.Kind),
                        Literal(() => entry.Value, BlobKind.CustomDebugInformation, (r, h) =>
                        {
                            var blob = r.GetBlobBytes(h);
                            int length = blob.Length;
                            string suffix = "";

                            if (blob.Length > BlobSizeLimit)
                            {
                                length = BlobSizeLimit;
                                suffix = "-...";
                            }

                            return BitConverter.ToString(blob, 0, length) + suffix;
                        })
                    },
                    details: TryDecodeCustomDebugInformation(entry)
                );
            }

            WriteTable(table);
        }

        public string TryDecodeCustomDebugInformation(CustomDebugInformation entry)
        {
            Guid kind;
            BlobReader blobReader;

            try
            {
                kind = _reader.GetGuid(entry.Kind);
                blobReader = _reader.GetBlobReader(entry.Value);
            }
            catch
            {
                // error is already reported
                return null;
            }

            if (kind == PortableCustomDebugInfoKinds.SourceLink)
            {
                return VisualizeSourceLink(blobReader);
            }

            if (kind == CompilationMetadataReferences)
            {
                return VisualizeCompilationMetadataReferences(blobReader);
            }

            if (kind == CompilationOptions)
            {
                return VisualizeCompilationOptions(blobReader);
            }

            return null;
        }

        private static string VisualizeSourceLink(BlobReader reader)
            => reader.ReadUTF8(reader.RemainingBytes);

        private static string TryReadUtf8NullTerminated(ref BlobReader reader)
        {
            var terminatorIndex = reader.IndexOf(0);
            if (terminatorIndex == -1)
            {
                return null;
            }

            var value = reader.ReadUTF8(terminatorIndex);
            _ = reader.ReadByte();
            return value;
        }

        [Flags]
        private enum MetadataReferenceFlags
        {
            Assembly = 1,
            EmbedInteropTypes = 1 << 1,
        }

        private static string VisualizeCompilationMetadataReferences(BlobReader reader)
        {
            var table = new TableBuilder(
                title: null,
                "FileName",
                "Aliases",
                "Flags",
                "TimeStamp",
                "FileSize",
                "MVID")
            {
                HorizontalSeparatorChar = '-',
                Indent = "  ",
                FirstRowNumber = 0,
            };

            while (reader.RemainingBytes > 0)
            {
                var fileName = TryReadUtf8NullTerminated(ref reader);
                var aliases = TryReadUtf8NullTerminated(ref reader);

                string flags = null;
                string timeStamp = null;
                string fileSize = null;
                string mvid = null;
                
                try { flags = ((MetadataReferenceFlags)reader.ReadByte()).ToString(); } catch { }
                try { timeStamp = $"0x{reader.ReadUInt32():X8}"; } catch { }
                try { fileSize = $"0x{reader.ReadUInt32():X8}"; } catch { }
                try { mvid = reader.ReadGuid().ToString(); } catch { }

                table.AddRow(
                    (fileName != null) ? $"'{fileName}'" : BadMetadataStr,
                    (aliases != null) ? $"'{string.Join("', '", aliases.Split(','))}'" : BadMetadataStr,
                    flags ?? BadMetadataStr,
                    timeStamp ?? BadMetadataStr,
                    fileSize ?? BadMetadataStr,
                    mvid ?? BadMetadataStr
                );
            }

            var builder = new StringBuilder();
            builder.AppendLine("{");
            table.WriteTo(new StringWriter(builder));
            builder.AppendLine("}");
            return builder.ToString();
        }

        private static string VisualizeCompilationOptions(BlobReader reader)
        {
            var builder = new StringBuilder();
            builder.AppendLine("{");

            while (reader.RemainingBytes > 0)
            {
                var key = TryReadUtf8NullTerminated(ref reader);
                if (key == null)
                {
                    builder.AppendLine(BadMetadataStr);
                    break;
                }

                builder.Append($"  {key}: ");

                var value = TryReadUtf8NullTerminated(ref reader);
                if (value == null)
                {
                    builder.AppendLine(BadMetadataStr);
                    break;
                }

                builder.AppendLine(value);
            }

            builder.AppendLine("}");
            return builder.ToString();
        }

        public void VisualizeMethodBody(MethodBodyBlock body, MethodDefinitionHandle generationHandle, int generation)
        {
            var handle = (MethodDefinitionHandle)GetAggregateHandle(generationHandle, generation);
            var method = GetMethod(handle);
            VisualizeMethodBody(body, method, handle);
        }

        public void VisualizeMethodBody(MethodDefinitionHandle methodHandle, Func<int, MethodBodyBlock> bodyProvider)
        {
            var method = GetMethod(methodHandle);

            if ((method.ImplAttributes & MethodImplAttributes.CodeTypeMask) != MethodImplAttributes.Managed)
            {
                _writer.WriteLine("native code");
                return;
            }

            var rva = method.RelativeVirtualAddress;
            if (rva == 0)
            {
                return;
            }

            var body = bodyProvider(rva);
            VisualizeMethodBody(body, method, methodHandle);
        }

        private void VisualizeMethodBody(MethodBodyBlock body, MethodDefinition method, MethodDefinitionHandle methodHandle)
        {
            StringBuilder builder = new StringBuilder();

            // TODO: Inspect EncLog to find a containing type and display qualified name.
            builder.AppendFormat("Method {0} (0x{1:X8})", Literal(() => method.Name), MetadataTokens.GetToken(methodHandle));
            builder.AppendLine();

            if (!body.LocalSignature.IsNil)
            {
                builder.AppendFormat("  Locals: {0}", StandaloneSignature(() => GetLocalSignature(body.LocalSignature)));
                builder.AppendLine();
            }

            ILVisualizer.Default.DumpMethod(
                builder,
                body.MaxStack,
                body.GetILContent(),
                ImmutableArray.Create<ILVisualizer.LocalInfo>(),     // TODO
                ImmutableArray.Create<ILVisualizer.HandlerSpan>());  // TODO: ILVisualizer.GetHandlerSpans(body.ExceptionRegions)

            builder.AppendLine();

            _writer.Write(builder.ToString());
        }

        public void WriteLine(string line)
        {
            _writer.WriteLine(line);
        }

        private sealed class TokenTypeComparer : IComparer<EntityHandle>
        {
            public static readonly TokenTypeComparer Instance = new TokenTypeComparer();

            public int Compare(EntityHandle x, EntityHandle y)
            {
                return x.Kind.CompareTo(y.Kind);
            }
        }
    }
}
