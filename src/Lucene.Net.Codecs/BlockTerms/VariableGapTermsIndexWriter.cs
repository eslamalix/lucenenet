using Lucene.Net.Index;
using Lucene.Net.Store;
using Lucene.Net.Util;
using Lucene.Net.Util.Fst;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Lucene.Net.Codecs.BlockTerms
{
    /*
     * Licensed to the Apache Software Foundation (ASF) under one or more
     * contributor license agreements.  See the NOTICE file distributed with
     * this work for additional information regarding copyright ownership.
     * The ASF licenses this file to You under the Apache License, Version 2.0
     * (the "License"); you may not use this file except in compliance with
     * the License.  You may obtain a copy of the License at
     *
     *     http://www.apache.org/licenses/LICENSE-2.0
     *
     * Unless required by applicable law or agreed to in writing, software
     * distributed under the License is distributed on an "AS IS" BASIS,
     * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
     * See the License for the specific language governing permissions and
     * limitations under the License.
     */

    /// <summary>
    /// Selects index terms according to provided pluggable
    /// {@link IndexTermSelector}, and stores them in a prefix trie that's
    /// loaded entirely in RAM stored as an FST.  This terms
    /// index only supports unsigned byte term sort order
    /// (unicode codepoint order when the bytes are UTF8).
    /// 
    /// @lucene.experimental
    /// </summary>
    public class VariableGapTermsIndexWriter : TermsIndexWriterBase
    {
        protected IndexOutput m_output;

        /** Extension of terms index file */
        internal const string TERMS_INDEX_EXTENSION = "tiv";
        internal const string CODEC_NAME = "VARIABLE_GAP_TERMS_INDEX";
        internal const int VERSION_START = 0;
        internal const int VERSION_APPEND_ONLY = 1;
        internal const int VERSION_CHECKSUM = 2;
        internal const int VERSION_CURRENT = VERSION_CHECKSUM;

        private readonly List<FstFieldWriter> _fields = new List<FstFieldWriter>();

        private readonly IndexTermSelector _policy;

        /// <summary>
        /// Hook for selecting which terms should be placed in the terms index
        /// 
        /// IsIndexTerm for each term in that field
        /// NewField is called at the start of each new field
        /// 
        /// @lucene.experimental
        /// </summary>
        public abstract class IndexTermSelector
        {
            /// <summary>
            /// Called sequentially on every term being written
            /// returning true if this term should be indexed
            /// </summary>
            public abstract bool IsIndexTerm(BytesRef term, TermStats stats);

            /// <summary>Called when a new field is started</summary>
            public abstract void NewField(FieldInfo fieldInfo);
        }

        /// <remarks>
        /// Same policy as {@link FixedGapTermsIndexWriter}
        /// </remarks>
        public sealed class EveryNTermSelector : IndexTermSelector
        {
            private int _count;
            private readonly int _interval;

            public EveryNTermSelector(int interval)
            {
                this._interval = interval;
                _count = interval; // First term is first indexed term
            }

            public override bool IsIndexTerm(BytesRef term, TermStats stats)
            {
                if (_count >= _interval) 
                {
                    _count = 1;
                    return true;
                }
                
                _count++;
                return false;
            }

            public override void NewField(FieldInfo fieldInfo)
            {
                _count = _interval;
            }
        }

        /// <summary>
        /// Sets an index term when docFreq >= docFreqThresh, or
        /// every interval terms.  This should reduce seek time
        /// to high docFreq terms. 
        /// </summary>
        public sealed class EveryNOrDocFreqTermSelector : IndexTermSelector
        {
            private int _count;
            private readonly int _docFreqThresh;
            private readonly int _interval;

            public EveryNOrDocFreqTermSelector(int docFreqThresh, int interval)
            {
                _interval = interval;
                _docFreqThresh = docFreqThresh;
                _count = interval; // First term is first indexed term
            }

            public override bool IsIndexTerm(BytesRef term, TermStats stats)
            {
                if (stats.DocFreq >= _docFreqThresh || _count >= _interval)
                {
                    _count = 1;
                    return true;
                }
                
                _count++;
                return false;
            }

            public override void NewField(FieldInfo fieldInfo)
            {
                _count = _interval;
            }
        }

        // TODO: it'd be nice to let the FST builder prune based
        // on term count of each node (the prune1/prune2 that it
        // accepts), and build the index based on that.  This
        // should result in a more compact terms index, more like
        // a prefix trie than the other selectors, because it
        // only stores enough leading bytes to get down to N
        // terms that may complete that prefix.  It becomes
        // "deeper" when terms are dense, and "shallow" when they
        // are less dense.
        //
        // However, it's not easy to make that work this this
        // API, because that pruning doesn't immediately know on
        // seeing each term whether that term will be a seek point
        // or not.  It requires some non-causality in the API, ie
        // only on seeing some number of future terms will the
        // builder decide which past terms are seek points.
        // Somehow the API'd need to be able to return a "I don't
        // know" value, eg like a Future, which only later on is
        // flipped (frozen) to true or false.
        //
        // We could solve this with a 2-pass approach, where the
        // first pass would build an FSA (no outputs) solely to
        // determine which prefixes are the 'leaves' in the
        // pruning. The 2nd pass would then look at this prefix
        // trie to mark the seek points and build the FST mapping
        // to the true output.
        //
        // But, one downside to this approach is that it'd result
        // in uneven index term selection.  EG with prune1=10, the
        // resulting index terms could be as frequent as every 10
        // terms or as rare as every <maxArcCount> * 10 (eg 2560),
        // in the extremes.

        public VariableGapTermsIndexWriter(SegmentWriteState state, IndexTermSelector policy)
        {
            string indexFileName = IndexFileNames.SegmentFileName(state.SegmentInfo.Name, state.SegmentSuffix,
                TERMS_INDEX_EXTENSION);
            m_output = state.Directory.CreateOutput(indexFileName, state.Context);
            bool success = false;

            try
            {
                _policy = policy;
                WriteHeader(m_output);
                success = true;
            }
            finally
            {
                if (!success)
                    IOUtils.CloseWhileHandlingException(m_output);
            }
        }

        private void WriteHeader(IndexOutput output)
        {
            CodecUtil.WriteHeader(output, CODEC_NAME, VERSION_CURRENT);
        }

        public override FieldWriter AddField(FieldInfo field, long termsFilePointer)
        {
            _policy.NewField(field);
            var writer = new FstFieldWriter(this, field, termsFilePointer);
            _fields.Add(writer);
            return writer;
        }

        /// <remarks>
        /// Note: If your codec does not sort in unicode code point order,
        /// you must override this method to simplly return IndexedTerm.Length
        /// </remarks>
        protected virtual int IndexedTermPrefixLength(BytesRef priorTerm, BytesRef indexedTerm)
        {
            // As long as codec sorts terms in unicode codepoint
            // order, we can safely strip off the non-distinguishing
            // suffix to save RAM in the loaded terms index.

            int idxTermOffset = indexedTerm.Offset;
            int priorTermOffset = priorTerm.Offset;
            int limit = Math.Min(priorTerm.Length, indexedTerm.Length);
            for (int byteIdx = 0; byteIdx < limit; byteIdx++)
            {
                if (priorTerm.Bytes[priorTermOffset + byteIdx] != indexedTerm.Bytes[idxTermOffset + byteIdx])
                {
                    return byteIdx + 1;
                }
            }

            return Math.Min(1 + priorTerm.Length, indexedTerm.Length);
        }

        private class FstFieldWriter : FieldWriter
        {
            private readonly VariableGapTermsIndexWriter outerInstance;

            private readonly Builder<long?> _fstBuilder;
            //private readonly PositiveIntOutputs fstOutputs; // LUCENENET NOTE: Not used
            private readonly long _startTermsFilePointer;

            internal FieldInfo FieldInfo { get; private set; }
            internal FST<long?> Fst { get; private set; }
            internal long IndexStart { get; private set; }

            private readonly BytesRef _lastTerm = new BytesRef();
            private bool _first = true;

            public FstFieldWriter(VariableGapTermsIndexWriter outerInstance, FieldInfo fieldInfo, long termsFilePointer)
            {
                this.outerInstance = outerInstance;
                FieldInfo = fieldInfo;
                PositiveIntOutputs fstOutputs = PositiveIntOutputs.Singleton;
                _fstBuilder = new Builder<long?>(FST.INPUT_TYPE.BYTE1, fstOutputs);
                IndexStart = this.outerInstance.m_output.FilePointer;

                // Always put empty string in
                _fstBuilder.Add(new IntsRef(), termsFilePointer);
                _startTermsFilePointer = termsFilePointer;
            }

            public override bool CheckIndexTerm(BytesRef text, TermStats stats)
            {
                // NOTE: we must force the first term per field to be
                // indexed, in case policy doesn't:
                if (outerInstance._policy.IsIndexTerm(text, stats) || _first)
                {
                    _first = false;
                    return true;
                }

                _lastTerm.CopyBytes(text);
                return false;
            }

            private readonly IntsRef _scratchIntsRef = new IntsRef();

            public override void Add(BytesRef text, TermStats stats, long termsFilePointer)
            {
                if (text.Length == 0)
                {
                    // We already added empty string in ctor
                    Debug.Assert(termsFilePointer == _startTermsFilePointer);
                    return;
                }
                int lengthSave = text.Length;
                text.Length = outerInstance.IndexedTermPrefixLength(_lastTerm, text);
                try
                {
                    _fstBuilder.Add(Util.Fst.Util.ToIntsRef(text, _scratchIntsRef), termsFilePointer);
                }
                finally
                {
                    text.Length = lengthSave;
                }
                _lastTerm.CopyBytes(text);
            }

            public override void Finish(long termsFilePointer)
            {
                Fst = _fstBuilder.Finish();
                if (Fst != null)
                    Fst.Save(outerInstance.m_output);
            }
        }

        public override void Dispose()
        {
            if (m_output == null) return;

            try
            {
                long dirStart = m_output.FilePointer;
                int fieldCount = _fields.Count;

                int nonNullFieldCount = 0;
                for (int i = 0; i < fieldCount; i++)
                {
                    FstFieldWriter field = _fields[i];
                    if (field.Fst != null)
                    {
                        nonNullFieldCount++;
                    }
                }

                m_output.WriteVInt(nonNullFieldCount);
                for (int i = 0; i < fieldCount; i++)
                {
                    FstFieldWriter field = _fields[i];
                    if (field.Fst != null)
                    {
                        m_output.WriteVInt(field.FieldInfo.Number);
                        m_output.WriteVLong(field.IndexStart);
                    }
                }
                WriteTrailer(dirStart);
                CodecUtil.WriteFooter(m_output);
            }
            finally
            {
                m_output.Dispose();
                m_output = null;
            }
        }

        private void WriteTrailer(long dirStart)
        {
            m_output.WriteLong(dirStart);
        }
    }
}