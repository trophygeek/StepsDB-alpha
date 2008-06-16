﻿// Copyright (C) 2008, by David W. Jeske
// All Rights Reserved.


using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Bend
{




    
    // ---------------[ Main ]---------------------------------------------------------


    class Program
    {
        static void Main(string[] args)
        {
            // Util.utilStruct_test();

            LayerManager db = new LayerManager(InitMode.NEW_REGION,"c:\\test");
            

            db.setValue("test/3","a");
            db.setValue("test/2","b");
            db.setValue("test/1","c");
            db.debugDump();

            db.flushWorkingSegment();    // this will flush and read the current segment


            Console.WriteLine("--- after flush");

            db.debugDump();

            Console.WriteLine("press any key...");
            Console.ReadKey();
        }
    }
}
