﻿// Copyright (C) 2008, by David W. Jeske
// All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Reflection;

// TODO: eliminate the dual paths for "manual apply" and "log apply"... make it always just do log apply

namespace Bend
{

    public enum InitMode
    {
        NEW_REGION,    // initialize the region fresh, error if it looks like there is a region still there
        RESUME         // standard resume/recover from an existing region
    }

    // ---------------[ LayerManager ]---------------------------------------------------------

    public class LayerManager : IDisposable 
    {
        internal List<SegmentMemoryBuilder> segmentlayers;  // newest to oldest list of the in-memory segments
        internal SegmentMemoryBuilder workingSegment;

        internal String dir_path;   // should change this to not assume directories/files
        public  IRegionManager regionmgr;
        internal List<WeakReference<Txn>> pending_txns;
        RangemapManager rangemapmgr;
        FreespaceManager freespacemgr;

        LogWriter logwriter;
        Receiver receiver;

        // constructors ....

        public LayerManager() {
            pending_txns = new List<WeakReference<Txn>>();

            segmentlayers = new List<SegmentMemoryBuilder>();   // a list of segment layers, newest to oldest
            workingSegment = new SegmentMemoryBuilder();
            segmentlayers.Add(workingSegment);
            
        }

        public LayerManager(InitMode mode, String dir_path)
            : this() {
            this.dir_path = dir_path;


            if (mode == InitMode.NEW_REGION) {
                // right now we only have one region type
                regionmgr = new RegionExposedFiles(InitMode.NEW_REGION, dir_path);

                // get our log online...
                logwriter = new LogWriter(InitMode.NEW_REGION, regionmgr);

                // setup the initial numgenerations record
                RangemapManager.Init(this);
                // TODO: do something sane with initial freespace setup
            } else if (mode == InitMode.RESUME) {
                regionmgr = new RegionExposedFiles(dir_path);
                receiver = new Receiver(this);
                logwriter = new LogWriter(InitMode.RESUME, regionmgr, receiver);
            } else {
                throw new Exception("unknown init mode");
            }

            // once the rangemap is initialized we can actually READ data!!
            rangemapmgr = new RangemapManager(this);   
            freespacemgr = new FreespaceManager(this);
        }

        // inner classes ...
        public struct Receiver : ILogReceiver
        {
            LayerManager mylayer;
            SegmentMemoryBuilder checkpointSegment;
            public Receiver(LayerManager mylayer) {
                this.mylayer = mylayer;
                checkpointSegment = null;
            }
            public void handleCommand(byte cmd, byte[] cmddata) {
                if (cmd == (byte)LogCommands.UPDATE) {
                    // decode basic block key/value writes
                    BlockAccessor ba = new BlockAccessor(cmddata);
                    ISegmentBlockDecoder decoder = new SegmentBlockBasicDecoder(ba);
                    foreach (KeyValuePair<RecordKey, RecordUpdate> kvp in decoder.sortedWalk()) {
                        // populate our working segment
                        lock (mylayer.segmentlayers) {
                            mylayer.workingSegment.setRecord(kvp.Key, kvp.Value);
                        }
                    }
                } else if (cmd == (byte)LogCommands.CHECKPOINT) {
                    // TODO: we need some kind of key/checksum to be sure that we CHECKPOINT and DROP the right data
                    checkpointSegment = mylayer.workingSegment;
                    SegmentMemoryBuilder newsegment = new SegmentMemoryBuilder();
                    lock (mylayer.segmentlayers) {
                        mylayer.workingSegment = newsegment;
                        mylayer.segmentlayers.Insert(0, mylayer.workingSegment);
                    }
                } else if (cmd == (byte)LogCommands.CHECKPOINT_DROP) {
                    // TODO: we need some kind of key/checksum to be sure that we CHECKPOINT and DROP the right data
                    if (checkpointSegment != null) {
                        lock (mylayer.segmentlayers) {
                            mylayer.segmentlayers.Remove(checkpointSegment);
                        }
                    } else {
                        throw new Exception("can't drop, no segment to drop");
                    }
                } else {
                    throw new Exception("unimplemented command");
                }
            }
        }

        enum LogCommands
        {
            UPDATE = 0,
            CHECKPOINT = 1,
            CHECKPOINT_DROP = 2
        }

        public class Txn : IDisposable
        {
            LayerManager mylayer;
            long tsn; // transaction sequence number
            long last_logwaitnumber = 0;
            enum TxnState
            {
                PENDING,
                PREPARED,
                COMMITTED,
                ABORTED
            }
            TxnState state = TxnState.PENDING;
            internal Txn(LayerManager _layer) {
                this.mylayer = _layer;
                this.tsn = System.DateTime.Now.ToBinary();
                // TODO: store the stack backtrace of who created this if we're in debug mode
            }

            public void setValue(RecordKey key, RecordUpdate update) {
                // build a byte[] for the updates using the basic block encoder
                {
                    MemoryStream writer = new MemoryStream();
                    ISegmentBlockEncoder encoder = new SegmentBlockBasicEncoder();
                    encoder.setStream(writer);
                    encoder.add(key, update);
                    encoder.flush();
                    mylayer.logwriter.addCommand((byte)LogCommands.UPDATE, writer.ToArray(), 
                        ref this.last_logwaitnumber);

                }
                // TODO: switch our writes to always occur through "handling the log"
                // TODO: make our writes only visible to US, by creating a "transaction segment"
                lock (mylayer.segmentlayers) {
                    mylayer.workingSegment.setRecord(key, update); // add to working set
                }
            }

            public void setValueParsed(String skey, String svalue) {
                RecordKey key = new RecordKey();
                key.appendParsedKey(skey);
                RecordUpdate update = RecordUpdate.WithPayload(svalue);

                this.setValue(key, update);
            }
            public void addCommand(byte cmd, byte[] cmddata) {
                mylayer.logwriter.addCommand(cmd, cmddata, ref this.last_logwaitnumber);
            }

            public void commit() {
                // TODO: commit should be what finalizes pending writes into final writes
                //     and cleans up locks and state

                // TODO: this flush concept is slighly broken. It assumes we are the only
                //   thread adding anything to the logwriter. We should probably hand
                //   logwriter this entire object and ask it to flush, so it could be holding
                //   multiple valid TXs. Currently if someone else is in the middle of
                //   adding elements to the log, some of their stuff will be flushed with this
                //   flush, while others will go in the next flush -- BAD.

                if (this.last_logwaitnumber != 0) {
                    mylayer.logwriter.flushPendingCommandsThrough(last_logwaitnumber);
                }

                state = TxnState.COMMITTED;
            }
            public void abort() {
                state = TxnState.ABORTED;
            }
            public void Dispose() {
                if (state == TxnState.PENDING) {
                    throw new Exception("disposed Txn still pending " + this.tsn);
                }
            }
        }

        // impl ....

        public Txn newTxn() {
            Txn newtx = new Txn(this);
            pending_txns.Add(new WeakReference<Txn>(newtx));  // make sure we don't prevent collection
            return newtx;
        }
        private int SEGMENT_BLOCKSIZE = 4 * 1024 * 1024;  // 4 MB

        private void _writeSegment(Txn tx, IEnumerable<KeyValuePair<RecordKey, RecordUpdate>> records, int gen_num) {
            // write the checkpoint segment and flush
            // TODO: make this happen in the background!!
            SegmentWriter segmentWriter = new SegmentWriter(records);


            while (segmentWriter.hasMoreData()) {
                // allocate new segment address from freespace
                IRegion writer = freespacemgr.allocateNewSegment(tx, SEGMENT_BLOCKSIZE);
                Stream wstream = writer.getNewAccessStream();
                SegmentWriter.WriteInfo wi = segmentWriter.writeToStream(wstream);
                wstream.Flush();
                wstream.Close();

                // reopen the segment for reading
                IRegion reader = regionmgr.readRegionAddr((uint)writer.getStartAddress());


                // record the index pointer (geneneration and rangekey -> block address)
                // rangemapmgr.newGeneration(tx, reader);   // add the checkpoint segment to the rangemap
                rangemapmgr.mapGenerationToRegion(tx, gen_num, wi.start_key, wi.end_key, reader);

            }                
            

        }

        public void flushWorkingSegment() {
            // create a new working segment            
            SegmentMemoryBuilder newlayer = new SegmentMemoryBuilder();
            SegmentMemoryBuilder checkpointSegment;
            int checkpoint_segment_size;

            // grab the checkpoint segment and move it aside
            lock (this.segmentlayers) {
                checkpointSegment = workingSegment;
                checkpoint_segment_size = checkpointSegment.RowCount;
                workingSegment = newlayer;
                segmentlayers.Insert(0, workingSegment);                
            }

            { // TODO: does this need to be in the lock?
                byte[] emptydata = new byte[0];
                long logWaitNumber = 0;
                this.logwriter.addCommand((byte)LogCommands.CHECKPOINT, emptydata, ref logWaitNumber);
            }

            
            
            {
                Txn tx = new Txn(this);

                // allocate a new generation number
                int new_generation_number = rangemapmgr.allocNewGeneration(tx);

                this._writeSegment(tx, checkpointSegment.sortedWalk(), new_generation_number); 
    
                {
                    byte[] emptydata = new byte[0];
                    tx.addCommand((byte)LogCommands.CHECKPOINT_DROP, emptydata);
                }
                tx.commit();                             // commit the freespace and rangemap transaction

            }

            // TODO: make this atomic            
            
            // FIXME: don't do this anymore, because we are now rangemap walking!!
              // re-read the segment and use it to replace the checkpoint segment            
              // SegmentReader sr = new SegmentReader(reader.getStream());
              // segmentlayers.Insert(1, sr); // working segment is zero, so insert this after it
            
            
            // reader.getStream().Close(); // force close the reader

            lock (this.segmentlayers) {
                if (checkpointSegment.RowCount != checkpoint_segment_size) {
                    System.Console.WriteLine("checkpointSegment was added to while checkpointing!! lost {0} rows",
                        checkpointSegment.RowCount - checkpoint_segment_size);
                }
                segmentlayers.Remove(checkpointSegment);
            }
        }

        public void mergeAllSegments() {

            // (1) get a handle to all the segments we wish to merge
            // TODO: delegate to RangemapManager to give us the segments

            int gen_count = rangemapmgr.genCount();

            if (gen_count < 1) {
                // nothing to even reprocess
                return;
            }

            List<KeyValuePair<RecordKey, RecordData>> genpointers = new List<KeyValuePair<RecordKey, RecordData>>();

            RecordKey start_key = new RecordKey().appendParsedKey(".ROOT/GEN");
            RecordKey cur_key = start_key;
            RecordKey found_key = new RecordKey();
            RecordData found_record = new RecordData(RecordDataState.NOT_PROVIDED,found_key);
            while (rangemapmgr.getNextRecord(cur_key, ref found_key, ref found_record, false) == GetStatus.PRESENT) {
                // check that the first two keyparts match
                if (found_key.isSubkeyOf(start_key)) {
                    genpointers.Add(new KeyValuePair<RecordKey,RecordData>(found_key,found_record));
                    cur_key = found_key;
                } else {
                    // we're done matching the generation records
                    break;
                }               
            }

            
            // (2) now we iterate through the generation pointers, building the merge chain
            IEnumerable<KeyValuePair<RecordKey, RecordUpdate>> chain = null;
            foreach (KeyValuePair<RecordKey,RecordData> kvp in genpointers) {
                
                IEnumerable<KeyValuePair<RecordKey,RecordUpdate>> nextchain = 
                    rangemapmgr.getSegmentFromMetadata(kvp.Value).sortedWalk();
                if (chain == null) {
                    chain = nextchain;
                } else {
                    chain = SortedMergeExtension.MergeSort(nextchain,chain,true);  // merge sort keeps keys on the left
                }                                
            }

            // (3) now perform the merge!!
            {
                Txn tx = new Txn(this);


                // allocate a new generation number
                // int new_generation_number = rangemapmgr.allocNewGeneration(tx);
                // FIXME: can we write this as a zero generation?
                this._writeSegment(tx, chain, 0);
                

                // remove the old segment mappings
                foreach (KeyValuePair<RecordKey, RecordData> kvp in genpointers) {
                    rangemapmgr.unmapSegment(tx, kvp.Key, kvp.Value);
                }

                rangemapmgr.setGenerationCountToZeroHack();     // check to see if we can shrink NUMGENERATIONS
                
                tx.commit();                             // commit the freespace and rangemap transaction

                rangemapmgr.clearSegmentCacheHack();
                // reader.getStream().Close();              // force close the reader
                
            }

        }

        public GetStatus getRecord(RecordKey key, out RecordData record) {
            RecordKey found_key = new RecordKey();
            record = new RecordData(RecordDataState.NOT_PROVIDED, new RecordKey());
            if (rangemapmgr.getNextRecord(key, ref found_key, ref record,true) == GetStatus.PRESENT) {
                if (found_key.Equals(key)) {
                    return GetStatus.PRESENT;
                }
            }
            record = null;
            return GetStatus.MISSING;
        }

        public GetStatus getNextRecord(RecordKey lowkey, ref RecordKey found_key, ref RecordData found_record) {
            return (rangemapmgr.getNextRecord(lowkey, ref found_key, ref found_record,false));
        }

        public void debugDump()
        {
                
            foreach (ISortedSegment layer in segmentlayers) {
                Console.WriteLine("--- Memory Layer : " + layer.GetHashCode());
                debugDump(layer, "  ", new HashSet<string>());
            }
        }

        
        private void debugDump(ISortedSegment seg, String indent, HashSet<string> seenGenerations) {
            HashSet<string> nextSeenGenerations = new HashSet<string>(seenGenerations);
            RecordKey genkey = new RecordKey().appendParsedKey(".ROOT/GEN");

            // first, print all our keys
            foreach (KeyValuePair<RecordKey, RecordUpdate> kvp in seg.sortedWalk()) {
                Console.WriteLine(indent + kvp.Key + " : " + kvp.Value);
                
                if (kvp.Key.isSubkeyOf(genkey)) {
                    nextSeenGenerations.Add(kvp.Key.ToString());
                }
                
            }

            // second, walk the rangemap
            foreach (KeyValuePair<RecordKey, RecordUpdate> kvp in seg.sortedWalk()) {
                // see if this is a range key (i.e.   .ROOT/GEN/###/</>   )
                // .. if so, recurse

                if (kvp.Key.isSubkeyOf(genkey) && kvp.Value.type == RecordUpdateTypes.FULL) {
                    if (seenGenerations.Contains(kvp.Key.ToString())) {
                        Console.WriteLine("--- Skipping Tombstoned layer for Key " + kvp.Key.ToString());
                    } else {
                        Console.WriteLine("--- Layer for Keys: " + kvp.Key.ToString());
                        ISortedSegment newseg = rangemapmgr.getSegmentFromMetadata(kvp.Value);
                        debugDump(newseg, indent + " ",nextSeenGenerations);
                    }
                }
            }

        }


        public void setValueParsed(String skey, String svalue)
        {
            Txn implicit_txn = this.newTxn();
            implicit_txn.setValueParsed(skey, svalue);
            implicit_txn.commit();            
        }

        public void setValue(RecordKey key, RecordUpdate value) {
            Txn implicit_txn = this.newTxn();
            implicit_txn.setValue(key, value);
            implicit_txn.commit();            
        }

        public void Dispose() {
            if (logwriter != null) {
                logwriter.Dispose(); logwriter = null;
            }

            foreach (ISortedSegment segment in segmentlayers) {
                segment.Dispose();
            }
        }
    }




}