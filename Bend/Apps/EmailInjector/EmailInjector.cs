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
using System.Collections.Generic;
using System.Windows.Forms;

using System.Threading;

using LumiSoft.Net.Mime;
using anmar.SharpMimeTools;

using Bend.Indexer;

// TODO: put this in a different namespace from Bend, in a separate build target

// http://stackoverflow.com/questions/903711/reading-an-mbox-file-in-c
// http://www.codeproject.com/KB/cs/mime_project.aspx?print=true

namespace Bend.EmailIndexerTest {
     
   
    public class EmailInjector {

        LayerManager db;
        DbgGUI gui;
        TextIndexer indexer;


        [STAThread]
        static void Main(string[] args) {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            var window = new DbgGUI();
            window.SetDesktopLocation(700, 200);

            Thread newThread = new Thread(delegate() {
                EmailInjector.do_test(window, args);
            });
            newThread.Start();
            Application.Run(window);
        }

        public static void do_test(DbgGUI window, string[] args) {
            if (args.Length < 1) {
                Console.WriteLine("Usage:\n  index - clear the db and index email\n   search - perform search tests");
                Environment.Exit(1);
            }
            if (args[0].CompareTo("index") == 0) {
                LayerManager db = new LayerManager(InitMode.NEW_REGION, @"c:\EmailTest\DB");
                db.startMaintThread();
                EmailInjector injector = new EmailInjector(db, window);
                injector.parse_email_messages();
                injector.indexer.find_email_test();
            } else if (args[0].CompareTo("search") == 0) {
                LayerManager db = new LayerManager(InitMode.RESUME, @"c:\EmailTest\DB");
                EmailInjector injector = new EmailInjector(db, window);
                window.debugDump(db);
                injector.indexer.find_email_test();
            } else if (args[0].CompareTo("merge") == 0) {
                LayerManager db = new LayerManager(InitMode.RESUME, @"c:\EmailTest\DB");
                window.debugDump(db);
                // merge...                
                for (int x = 0; x < 30; x++) {
                    var mc = db.rangemapmgr.mergeManager.getBestCandidate();
                    window.debugDump(db, mc);
                    if (mc == null) {
                        Console.WriteLine("no more merge candidates.");
                        break;                        
                    }
                    db.performMerge(mc);
                    window.debugDump(db);
                }
            } else if (args[0].CompareTo("test") == 0) {
                LayerManager db = new LayerManager(InitMode.RESUME, @"c:\EmailTest\DB");
                window.debugDump(db);
                var key1 = new RecordKey()
                    .appendParsedKey(@".zdata/index/which/c:\EmailTest\Data\Sent:5441/10");
                var key2 = new RecordKey()
                    .appendParsedKey(@".zdata/index/zzn/c:\EmailTest\Data\saved_mail_2003:4962/385");

                var segkey = new RecordKey()
                    .appendParsedKey(".ROOT/GEN")
                    .appendKeyPart(new RecordKeyType_Long(1))
                    .appendKeyPart(key1)
                    .appendKeyPart(key2);

                var nextrow = db.FindNext(segkey,false);                

                Console.WriteLine("next: {0}",nextrow);

                var exactRow = db.FindNext(nextrow.Key, true);

                Console.WriteLine("refind: {0}", exactRow);
                    
            }
            Console.WriteLine("done....");
            
            Environment.Exit(0);
        }

        public EmailInjector(LayerManager db, DbgGUI gui) {
            this.db = db;
            this.gui = gui;
            this.indexer = new TextIndexer(db);
        }

        public static string UnixReadLine(Stream stream) {
            string line = "";
            while (stream.Position < stream.Length - 1) {

                int ch = stream.ReadByte();
                if (ch != 10) {
                    line = line + Char.ConvertFromUtf32(ch);
                } else {
                    return line;
                }
            }
            return "";
        }

        public void parse_msg(LayerWriteGroup txwg, string docid, string msgtxt, out int numwords) {
            if (msgtxt.Length > 4 * 1024) {
                msgtxt = msgtxt.Substring(0, 4 * 1024 - 1);
            }
            // db.setValueParsed(".zdata/doc/" + docid, msgtxt);
            // gui.debugDump(db);


#if true      
                // sharptools                
                SharpMessage msg = new anmar.SharpMimeTools.SharpMessage(msgtxt);
                System.Console.WriteLine("Subject: " + msg.Subject);

                indexer.index_document(txwg, docid, msg.Body, out numwords);
#else
                // LumiSoft                
                Mime msg = LumiSoft.Net.Mime.Mime.Parse(System.Text.Encoding.Default.GetBytes(msgtxt));
                System.Console.WriteLine("Subject: " + msg.MainEntity.Subject);
                indexer.index_document(txwg, docid, msg.MainEntity.DataText, out numwords);
#endif
      

            //foreach (SharpAttachment msgpart in msg.Attachments) {
            //    if (msgpart.MimeTopLevelMediaType == MimeTopLevelMediaType.text &&
            //        msgpart.MimeMediaSubType == "plain") {
            //        System.Console.WriteLine("Attachment: " + msgpart.Size);
            //   }
            //}                    
        }

        public void parse_email_messages() {
            string basepath = @"c:\EmailTest\Data";

            // http://www.csharp-examples.net/get-files-from-directory/
            string[] filePaths = Directory.GetFiles(basepath);

            long doc_count = 1;
            long word_count = 1;
            DateTime start = DateTime.Now;

            foreach (var fn in filePaths) {
                String fullpath = Path.Combine(basepath, fn);
                System.Console.WriteLine(fullpath);
                FileStream r = File.Open(fullpath, FileMode.Open, FileAccess.Read, FileShare.Read);
                BufferedStream reader = new BufferedStream(r);
                // http://msdn.microsoft.com/en-us/library/system.io.streamreader.readline.aspx

                List<string> lines = new List<string>();
                LayerWriteGroup txwg = new LayerWriteGroup(db,type:LayerWriteGroup.WriteGroupType.MEMORY_ONLY);                


                while (reader.Position < reader.Length - 1) {
                    string line = UnixReadLine(reader);
                    if (line.Length > 6 && line.Substring(0, 5) == "From ") {
                        if (lines.Count > 0) {
                            string msg = String.Join("\n", lines);
                            doc_count++;
                            
                            string docid = fullpath + ":" + doc_count;
                            int doc_numwords;
                            parse_msg(txwg, docid, msg, out doc_numwords);
                            word_count += doc_numwords;

                            DateTime cur = DateTime.Now;
                            double  elapsed_s = (cur - start).TotalSeconds;
                            Console.WriteLine("doc{0}: {1}    elapsed:{2}    docs/sec:{3}    words/sec:{4}",
                                doc_count, docid, elapsed_s, (float)doc_count / elapsed_s, (float)word_count / elapsed_s);
                            gui.debugDump(db);    

                            // end after a certain number of lines...
                            if (doc_count > 4000000000) {
                                goto end_now;
                            }

                        }
                        lines = new List<string>();
                    } else {
                        lines.Add(line);
                    }


                    if (doc_count % 50000 == 0) { gui.debugDump(db); }

                } // while adding docs
                
            } // foreach file

            Console.WriteLine("=================== EmailInjector end... time to fully optimize...");

            end_now:
            // we have to lock to assure we don't collide with the background merge thread
            lock (db) {
                // be sure to flush and merge before we search...
                db.flushWorkingSegment();
                gui.debugDump(db);
                for (int x = 0; x < 40; x++) {
                    var mc = db.rangemapmgr.mergeManager.getBestCandidate();
                    gui.debugDump(db, mc);
                    if (mc == null) { break; }
                    db.performMerge(mc);
                    gui.debugDump(db);
                }
            }
        }        

    }
}

