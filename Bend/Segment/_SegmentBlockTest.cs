﻿// Copyright (C) 2008, by David W. Jeske
// All Rights Reserved.

using System;
using System.IO;
using System.Collections.Generic;

using NUnit.Framework;
using Bend;

namespace BendPerfTest
{
    [TestFixture]
    public partial class A01_Block_Perf { }

}

namespace BendTests
{
    public interface IBlockTestFactory {
        ISegmentBlockEncoder makeEncoder();
        ISegmentBlockDecoder makeDecoder(BlockAccessor block);
    }

    public class SegmentBlock_Tests
    {
        public static void Block_Perftest(IBlockTestFactory factory) {
            // iterate through blocksizes, randomly generating input data, and then doing some
            // random key queries to see how fast retrieval is

            int[] block_sizes = { 2 * 1024, 40 * 1024, 100 * 1024, 2 * 1024 * 1024 };
            int[] value_sizes = { 4, 10, 30, 100, 1000, 10000 };
            int[] num_levels = { 2, 7, 15 };
            int[,] perf_results = new int[block_sizes.Length, value_sizes.Length];
            int READ_COUNT = 1000;

            Random rnd = new Random((int)DateTime.Now.ToBinary());

            foreach (int key_part_count in num_levels) {
                System.Console.WriteLine("--");
                foreach (int block_size in block_sizes) {
                    foreach (int value_size in value_sizes) {
                        ISegmentBlockEncoder enc = factory.makeEncoder();                        
                        MemoryStream ms = new MemoryStream();
                        enc.setStream(ms);

                        // encode the block
                        int curblock_size = 0;
                        while (curblock_size < block_size) {
                            // generate a random key

                            RecordKey key = new RecordKey();
                            for (int i = 0; i < key_part_count; i++) {
                                key.appendParsedKey("" + rnd.Next(0xFFFFFF));
                            }

                            // generate a random value
                            byte[] data = new byte[value_size];
                            for (int i = 0; i < value_size; i++) {
                                data[i] = (byte)rnd.Next(40, 50);
                            }
                            RecordUpdate upd = RecordUpdate.WithPayload(data);
                            curblock_size += key.encode().Length; 
                            curblock_size += value_size;

                            enc.add(key, upd);
                        }
                        enc.flush();

                        // init the decoder
                        ISegmentBlockDecoder dec = factory.makeDecoder(new BlockAccessor(ms.ToArray()));                        

                        System.GC.Collect();  // force GC so it may not happen during the test                    
                        // perform random access test
                        DateTime start = DateTime.Now;
                        for (int i = 0; i < READ_COUNT; i++) {
                            RecordKey key = new RecordKey();
                            for (int ki = 0; ki < key_part_count; ki++) {
                                key.appendParsedKey("" + rnd.Next(0xFFFFFF));
                            }

                            try {
                                dec.FindNext(key, true);
                            }
                            catch (KeyNotFoundException) {
                                // no problem
                            }
                        }
                        double duration_ms = (DateTime.Now - start).TotalMilliseconds;
                        double reads_per_second = (READ_COUNT * 1000.0) / (duration_ms);
                        System.Console.WriteLine("BlockSize src{0,10}  final{6,10}  ({7,10}), ValueSize {1,10}, Keyparts {5,10}, {2,10} reads in {3,10}ms,  {4,10} read/sec",
                            curblock_size, value_size, READ_COUNT, duration_ms, reads_per_second, 
                            key_part_count, ms.Length, ((double)ms.Length/(double)curblock_size)*(double)100.0 );
                    }
                }
            }

        } // testend


    }

}