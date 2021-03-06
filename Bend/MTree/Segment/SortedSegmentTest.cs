﻿// Copyright (C) 2008-2014 David W. Jeske
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or
// implied. See the License for the specific language governing
// permissions and limitations under the License. See the AUTHORS file
// for names of contributors.

using System;
using System.IO;
using NUnit.Framework;
using System.Collections.Generic;
using System.Linq;

using Bend;

namespace BendTests
{


    #region TestHelpers
    // this class adapts our pipe-qualifier to be able to test against record key
    public class QualAdaptor : IScanner<RecordKey>
    {
        PipeRowQualifier qual;

        

        public QualAdaptor(PipeRowQualifier qual) {
            this.qual = qual;
        }
        public bool MatchTo(RecordKey row_key) {
            IEnumerator<QualifierBase> qual_enum = qual.GetEnumerator();
            IEnumerator<RecordKeyType> key_enum = row_key.GetEnumeratorForKeyparts();

            bool qual_hasmore = qual_enum.MoveNext();
            bool key_hasmore = key_enum.MoveNext();

            bool last_result = true;
            while (qual_hasmore && key_hasmore && (last_result == true)) {
                QualifierBase q_part = qual_enum.Current;
                RecordKeyType_String key_part = (RecordKeyType_String)key_enum.Current;

                last_result = q_part.MatchTo(key_part.GetString());

                qual_hasmore = qual_enum.MoveNext();
                key_hasmore = key_enum.MoveNext();
            }

            return last_result;
        }

        public IComparable<RecordKey> genLowestKeyTest() {
            return this.qual.genLowestKey();
        }
        public IComparable<RecordKey> genHighestKeyTest() {
            return this.qual.genHighestKey();
        }
    }
    #endregion



    // ----------------------------------------- A02_SortedSegmentTests -----------------------------
    [TestFixture]
    public class A02_SortedSegmentTests
    {

        class TestRegionRead : IRegion
        {

            byte[] data;
            internal TestRegionRead(byte[] data) {
                this.data = data;
            }
            public Stream getNewAccessStream() {
                return new MemoryStream(data);
            }
            public BlockAccessor getNewBlockAccessor(int rel_block_start, int block_len) {
                byte[] data = new byte[block_len];
                Stream stream = this.getNewAccessStream();
                stream.Seek(rel_block_start,SeekOrigin.Begin);
                if (stream.Read(data,0,block_len) != block_len) {
                    throw new Exception("memory stream read returned partial");
                }
                return new BlockAccessor(data);
            }

            public long getStartAddress() {
                return 0;
            }
            public long getSize() {
                return data.Length;
            }
            public void Dispose() {
            }
        }

        class TestRegionWriteOnce : IRegion
        {
            internal MemoryStream mystream = null;
            long size = -1;
            internal TestRegionWriteOnce(long size) {
                this.size = size;
            }
            public Stream getNewAccessStream() {
                if (mystream == null) {
                    mystream = new MemoryStream();
                    mystream.SetLength(this.size);
                    return mystream;
                } else {
                    throw new Exception("you can only write to TestRegionWriteOnce, once!");
                }
            }
            public BlockAccessor getNewBlockAccessor(int rel_block_start,int len) {
                // not really a valid thing to do
                throw new Exception("not valid");
            }
            public long getSize() {
                if (mystream != null) {
                    return mystream.Length;
                } else {
                    return -1;
                }
            }
            public long getStartAddress() {
                return 0;
            }
            public void Dispose() {
            }
            
        }


        long TEST_BLOCK_LENGTH = 1 * 1024 * 1024; // 1MB

        [Test]
        public void T00_SegmentIndex_IndividualBlock_EncodeDecode() {
            string[] block_start_keys = { "test/1/2/3/4", "test/1/2/3/5" };
            int[] block_start_pos = { 0, 51 };
            int[] block_end_pos = { 50, 190 };

            byte[] streamdata;

            // write the index
            {
                MemoryStream st = new MemoryStream();

                // make a new index, and add some entries.
                SortedSegmentIndex index = new SortedSegmentIndex();
                for (int i = 0; i < block_start_keys.Length; i++) {
                    index.addMicroBlock(new RecordKey().appendParsedKey(block_start_keys[i]),
                        null, block_start_pos[i], block_end_pos[i]);
                }
                index.writeToStream(st);
                streamdata = st.ToArray();
            }

            // read it back
            {
                IRegion testregion = new TestRegionRead(streamdata);                
                SortedSegmentIndex index = new SortedSegmentIndex(streamdata, testregion);
                int pos = 0;
                foreach (KeyValuePair<RecordKey,SortedSegmentIndex._SegMicroBlockIndexEntry> kvp in index.microblocks) {
                    SortedSegmentIndex._SegMicroBlockIndexEntry block = kvp.Value;
                    Assert.AreEqual(new RecordKey().appendParsedKey(block_start_keys[pos]), block.lowest_key);
                    Assert.AreEqual(block_start_pos[pos], block.datastart);
                    Assert.AreEqual(block_end_pos[pos], block.dataend);
                    pos++;
                }
                Assert.AreEqual(pos, block_start_keys.Length, "index didn't return the right number of entries");
            }

        }


        [Test]
        public void T01_SortedSegment_ForwardScanTest() {

            // we need a: IEnumerable<KeyValuePair<RecordKey, RecordUpdate>> 
            var input_records = new BDSkipList<RecordKey, RecordUpdate>();
            string[] keys = { "a/b/c/d", "b/c/d/e" };

            var region_mgr = new RegionExposedFiles(InitMode.NEW_REGION,@"C:\BENDtst\SSScanTest_F");
            IRegion region_reader = null;

            int region_address = 1;

            foreach (var key in keys) {
                input_records.Add(new RecordKey().appendParsedKey(key), RecordUpdate.WithPayload("data:" + key));
            }

            // (1) OPEN A SEGMENT WRITER

            SegmentWriter segmentWriter = new SegmentWriter(input_records);

            while (segmentWriter.hasMoreData()) {
                // if (region_address != 0) {
                //     Assert.Fail("the keys didn't fit into a single segment, UGH!");
                // }
                DateTime start = DateTime.Now;
                // allocate new segment address from freespace
                IRegion writer = region_mgr.writeFreshRegionAddr(region_address++, 512 * 1024);                
                Stream wstream = writer.getNewAccessStream();
                SegmentWriter.WriteInfo wi = segmentWriter.writeToStream(wstream);

                wstream.Flush();  // TODO: flush at the end of all segment writing, not for each one
                wstream.Close();
                writer.Dispose(); // make sure the region closes

                double elapsed = (DateTime.Now - start).TotalSeconds;
                Console.WriteLine("segmentWritten with {0} keys in {1} seconds {2} keys/second",
                    wi.key_count, elapsed, (double)wi.key_count / elapsed);

                // reopen the segment for reading
                region_reader = region_mgr.readRegionAddrNonExcl(writer.getStartAddress());                
            }

            Assert.IsNotNull(region_reader, "UGH, region reader is null!");

            // (2) OPEN A SEGMENT READER
            var seg_reader = new SegmentReader(region_reader);

            int count = 0;
            foreach (var row in seg_reader.scanForward(ScanRange<RecordKey>.All())) {                
                Console.WriteLine("rec : " + row);                
                Assert.True(
                    row.Key.CompareTo(new RecordKey().appendParsedKey(keys[count++])) == 0,
                    "record key did not match");
            }
            Assert.AreEqual(keys.Length,count,"wrong number of records scanned");
            GC.Collect();
        }
        [Test]
        public void T01_SortedSegment_BackwardScanTest() {

            // we need a: IEnumerable<KeyValuePair<RecordKey, RecordUpdate>> 
            var input_records = new BDSkipList<RecordKey, RecordUpdate>();
            string[] keys = { "a/b/c/d", "b/c/d/e" };

            var region_mgr = new RegionExposedFiles(InitMode.NEW_REGION, @"C:\BENDtst\SSScanTest_B");
            IRegion region_reader = null;

            int region_address = 1;

            foreach (var key in keys) {
                input_records.Add(new RecordKey().appendParsedKey(key), RecordUpdate.WithPayload("data:" + key));
            }

            // (1) OPEN A SEGMENT WRITER

            SegmentWriter segmentWriter = new SegmentWriter(input_records);

            while (segmentWriter.hasMoreData()) {
                // if (region_address != 0) {
                //     Assert.Fail("the keys didn't fit into a single segment, UGH!");
                // }
                DateTime start = DateTime.Now;
                // allocate new segment address from freespace
                IRegion writer = region_mgr.writeFreshRegionAddr(region_address++, 512 * 1024);
                Stream wstream = writer.getNewAccessStream();
                SegmentWriter.WriteInfo wi = segmentWriter.writeToStream(wstream);

                wstream.Flush();  // TODO: flush at the end of all segment writing, not for each one
                wstream.Close();
                writer.Dispose(); // make sure the region closes

                double elapsed = (DateTime.Now - start).TotalSeconds;
                Console.WriteLine("segmentWritten with {0} keys in {1} seconds {2} keys/second",
                    wi.key_count, elapsed, (double)wi.key_count / elapsed);

                // reopen the segment for reading
                region_reader = region_mgr.readRegionAddrNonExcl(writer.getStartAddress());
            }

            Assert.IsNotNull(region_reader, "UGH, region reader is null!");

            // (2) OPEN A SEGMENT READER
            var seg_reader = new SegmentReader(region_reader);

            int count = keys.Length;
            foreach (var row in seg_reader.scanBackward(ScanRange<RecordKey>.All())) {
                Console.WriteLine("rec : " + row);
                Assert.True(
                    row.Key.CompareTo(new RecordKey().appendParsedKey(keys[--count])) == 0,
                    "record key did not match");
            }
            Assert.AreEqual(0, count, "wrong number of records scanned");
            GC.Collect();
        }




        [Test]
        public void T02_BuilderReader() {
            byte[] databuffer;
            

            // write the segment
            {
                MemoryStream ms = new MemoryStream();
                ms.SetLength(TEST_BLOCK_LENGTH);
                SegmentMemoryBuilder builder = new SegmentMemoryBuilder();
                builder.setRecord(new RecordKey().appendParsedKey("test/1"),
                    RecordUpdate.WithPayload("3"));



                SegmentWriter segmentWriter = new SegmentWriter(builder.sortedWalk());
                segmentWriter.writeToStream(ms);

                databuffer = ms.ToArray();
            }

            Assert.AreEqual(TEST_BLOCK_LENGTH, databuffer.Length, "Databuffer is not equal to blocklength");

            System.Console.WriteLine(databuffer);
            
            // segment readback
            {
                TestRegionRead testregion = new TestRegionRead(databuffer);
                System.Text.ASCIIEncoding enc = new System.Text.ASCIIEncoding();
                SegmentReader reader = new SegmentReader(testregion);

                // test the length of the block 

                // read back record
                RecordUpdate update;
                GetStatus status = reader.getRecordUpdate(new RecordKey().appendParsedKey("test/1"), out update);
                Assert.AreEqual(GetStatus.PRESENT, status);
                
                Assert.AreEqual("3", enc.GetString(update.data));
            }
        }


        // -------------------------------- RangeScan -----------------------------------
        // TODO: change this to happen without PipeQualifier, and pull those tests up to
        //   a LayerManager Integration level
        [Test]
        public void T03_RangeScan() {
            // TODO: remove this hacky converting from byte[] to string
            System.Text.ASCIIEncoding enc = new System.Text.ASCIIEncoding();

            SegmentMemoryBuilder builder = new SegmentMemoryBuilder();
            int records_written = 0;
            // generate a Pipe
            PipeStagePartition p =
                new PipeStagePartition("tablename",
                    new PipeStagePartition("id",
                        new PipeStageEnd()
                    )
                );


            // generate a set of data into a segment
            {
                // setup the HDF context
                PipeHdfContext ctx = new PipeHdfContext();
                ctx.setQualifier("tablename", "datatable");


                for (int i = 0; i < 1000; i += 30) {
                    ctx.setQualifier("id", enc.GetString(Lsd.numberToLsd(i, 10)));
                    // generate a PipeRowQualifier
                    PipeRowQualifier row = p.generateRowFromContext(ctx);

                    // produce a RecordKey
                    RecordKey key = new RecordKey();
                    foreach (QualifierBase qualpart in row) {
                        if (qualpart.GetType() == typeof(QualifierExact)) {
                            key.appendKeyPart(qualpart.ToString());
                        } else {
                            throw new Exception("only exactly qualifiers are allowed in row updates");
                        }
                    }
                    // put it in the memory segment
                    builder.setRecord(key, RecordUpdate.WithPayload(Lsd.numberToLsd(i, 10)));
                    records_written++;
                }
            }


            // VERIFY the buidler
            T03_RangeScan_Helper(builder, p, records_written, "SegmentMemoryBuilder");

            // write out a basic block segment and verify
            {
                // TODO: test with multiple advisors (lots of block boundaries, only one block, 
                //    .. one blocktype, multiple blocktypes...etc)

                TestRegionWriteOnce testwregion = new TestRegionWriteOnce(TEST_BLOCK_LENGTH);
                SegmentWriter writer = new SegmentWriter(builder.sortedWalk());
                writer.writeToStream(testwregion.getNewAccessStream());

                TestRegionRead testr_region = new TestRegionRead(testwregion.mystream.ToArray());     
                SegmentReader reader = new SegmentReader(testr_region);
                
                // VERIFY the reader
                T03_RangeScan_Helper(reader, p, records_written, "SegmentReader");
            }

        }

        

        public void T03_RangeScan_Helper(ISortedSegment segbase,PipeStagePartition p, int records_written, string title) {
            IScannable<RecordKey, RecordUpdate> segment = (IScannable<RecordKey, RecordUpdate>)segbase;
            // TODO: remove this hacky converting from byte[] to string
            System.Text.ASCIIEncoding enc = new System.Text.ASCIIEncoding();


            // TODO: test FindNext, FindPrev (false,true) cases...

            int max_i = 0;
            // scan FORWARD for a set of matching records (a subset of all records)
            {
                // .. build a context qualifier for the pipe
                PipeHdfContext ctx = new PipeHdfContext();
                ctx.setQualifier("tablename", "datatable");
                ctx.setQualifier("id", new QualifierAny());
                int records_scanned = 0;

                PipeRowQualifier qualifier = p.generateRowFromContext(ctx);
                int i = 0;
                foreach (KeyValuePair<RecordKey, RecordUpdate> kvp in segment.scanForward(new QualAdaptor(qualifier))) {
                    // check that the recordupdate equals our iterator
                    Assert.AreEqual(enc.GetString(Lsd.numberToLsd(i,10)), enc.GetString(kvp.Value.data), "scanForward, check recordupdate payload " + title);
                    max_i = Math.Max(i, max_i);
                    i = i + 30;
                    records_scanned++;
                }
                Assert.AreEqual(records_written, records_scanned, "scanForward: expect to read back the same number of records we wrote " + title);
                
            }

            // scan BACKWARD for a set of matching records (a subset of all records)
            {
                // .. build a context qualifier for the pipe
                PipeHdfContext ctx = new PipeHdfContext();
                ctx.setQualifier("tablename", "datatable");
                ctx.setQualifier("id", new QualifierAny());
                int records_scanned = 0;

                PipeRowQualifier qualifier = p.generateRowFromContext(ctx);
                int i = max_i;                
                foreach (KeyValuePair<RecordKey, RecordUpdate> kvp in segment.scanBackward(new QualAdaptor(qualifier))) {
                    // check that the recordupdate equals our iterator
                    Assert.AreEqual(enc.GetString(Lsd.numberToLsd(i, 10)), enc.GetString(kvp.Value.data), "scanBackward, check recordupdate payload " + title );
                    i = i - 30;
                    records_scanned++;
                }
                Assert.AreEqual(records_written, records_scanned, "scanBackward: expect to read back the same number of records we wrote " + title);

            }
        }

    }

    [TestFixture]
    public class ZZ_TODO_SortedSegment
    {
        [Test]
        public void T01_SortedSegment_TestDuplicateBlockStartKeys() {

            // do we allow equal startkeys in blocks? If so, our logic returns the last one, not the first
            //   FindNext("FOO",true)
            //      block 0:  "FOO" -> 1 record "FOO=1"
            //      block 1:  "FOO" -> 1 record "FOO=2"

            // do we handle a FindNext trying one block, and then having to try the next?
            //   FindNext("FOO",false)
            //      block 0:  "FOO" -> 1 record "FOO"
            //      block 1:  "FOO2" -> 1 record "FOO2"
            Assert.Fail("not implemented");
        }
        [Test]
        public void T00_SortedSegment_MultipleIndexEntry_ReadWrite() {
            Assert.Fail("not implemented");
        }

        [Test]
        public void T00_SortedSegment_MultipleBlockTypes_ReadWrite() {
            // probably should test the block GUID registry too
            Assert.Fail("not implemented");
        }
        [Test]
        public void T00_SortedFindNext_TypeRegistry() {
            // TODO: use a type registry to ask for the preferred implementation of 
            //   IScannableDictionary
            Assert.Fail("need to findnext");
        }

    }
}