﻿
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using Bend;

using System.IO;

using System.Threading;


/*
 * TODO: 
 * 
 * - don't let us touch keys until there is an instance id (either via INIT_NEW or JOIN_EXISTING)
 * - don't let us touch keys until the server is "active"
 * - make a simulated "server connect" so we can also have "disconnect"
 * - tail-log command (where we supply log pointers, error if the pointer is too old)
 * - fallback to key-copy if the tail-log didn't work
 * - make sure which server-log we process first doesn't change final records 
 *      (timestamp order log apply? per-record timestamp?)
 * 
 * Cases to Handle
 * 
 * read/write keys in our keyspace
 * push new writes to other clients, manage 'quorum agreed' pointer
 * check/pull logs from other servers when we join/connect
 * 
 * 
 * our keyspace schema
 * 
 *  _config/DATA-INSTANCE-ID = <guid of the dataset>
 *  _config/MY-SERVER-ID = <server guid>
 *  
 *  _config/quorum_write_requirement = <number of servers before we advance the repl tail>
 *  _config/log_max_age = <oldest loglines we want to keep.. 
 *                             note tombstones have to be forced to live this long>
 *  _config/log_max_size = <max log size in approximate bytes>
 * 
 *  _config/seeds/<SERVER GUID>
 *  
 *  _logs/<SERVER GUID>/<logid> -> [update info]
 *  
 *  _log_commit_heads/<SERVER_GUID> -> the newest log entry that is known to be committed for this server_guid
 *  
 * 
 * 
 */

namespace Bend {

    public class ServerConnector {
        Dictionary<string, ReplHandler> server_list = new Dictionary<string, ReplHandler>();
        public ReplHandler getServerHandle(string server_guid) {

            try {
                return server_list[server_guid];
            } catch (KeyNotFoundException) {
                throw new KeyNotFoundException("could not connect to server: " + server_guid);
            }
        }

        public void registerServer(string name, ReplHandler instance) {
            server_list[name] = instance;            
        }
        public void unregisterServer(string server_guid) {
            try {
                server_list.Remove(server_guid);
            } catch (KeyNotFoundException) {
                // nothing to remove
            }
        }

    }

    public class ServerContext {
        public string server_guid;
        public ServerConnector connector;
        public string prefix_hack;
    }

    public class ReplHandler {
        LayerManager db;
        ServerContext ctx;

        Random rnd;
        ReplPusher pusher;
        string data_instance_id;        

        public static Random myrnd = new Random();

        Thread worker;
        public enum ReplState {
            init,
            rebuild,
            active,
            shutdown
        };
        private ReplState state = ReplState.init;
        public ReplState State { get { return state; } }


        public override string ToString() {
            return String.Format("ReplHandler({0}:{1})", this.ctx.server_guid, this.state.ToString());
        }

        private ReplHandler(LayerManager db, ServerContext ctx) {
            this.db = db;
            this.rnd = new Random();
            this.pusher = new ReplPusher(this);
            this.ctx = ctx;

            
            try {

                var di_rk = new RecordKey().appendParsedKey(ctx.prefix_hack)
                    .appendKeyPart("_config")
                    .appendKeyPart("DATA-INSTANCE-ID");
                var rec = db.FindNext(di_rk,true);
                if (di_rk.CompareTo(rec.Key) != 0) {
                    throw new Exception(
                        String.Format("ReplHandler {0} , not able to fetch DATA-INSTANCE-ID", ctx.server_guid));
                }
                this.data_instance_id = rec.Value.ToString();
                Console.WriteLine("ReplHandler - {0}: data_instance_id {1}",
                    ctx.server_guid, data_instance_id);
            } catch (KeyNotFoundException) {
                throw new Exception("no data instance ID, try InitResume or InitJoin");
            }

            // check server_guid matches?

            // register ourself
            ctx.connector.registerServer(ctx.server_guid, this);

            // startup our background task
            worker = new Thread(delegate() {
                this.workerThread();
            });
            worker.Start();
        }

        public void Shutdown() {
            // remove us from the connector
            ctx.connector.unregisterServer(ctx.server_guid);
        }
        public class LogStatus {
            public string server_guid;
            public string log_commit_head;
            public string oldest_entry_pointer;
            // public string newest_pending_entry_pointer;
        }

        IEnumerable<LogStatus> getStatusForLogs() {
            var log_commit_heads_prefix = new RecordKey().appendParsedKey(ctx.prefix_hack)
                .appendKeyPart("_log_commit_heads");
            foreach (var log_head_row in db.scanForward(
                  new ScanRange<RecordKey>(log_commit_heads_prefix, 
                                            RecordKey.AfterPrefix(log_commit_heads_prefix), null))) {
                
                var server_guid = log_head_row.Key.key_parts[log_head_row.Key.key_parts.Count - 1];
                var log_commit_head = log_head_row.Value.ToString();
                
                var log_status = new LogStatus();
                log_status.server_guid = server_guid;
                log_status.log_commit_head = log_commit_head;                

                // first log entry for this log
                var oldestlogrow = db.FindNext(new RecordKey().appendParsedKey(ctx.prefix_hack)
                    .appendKeyPart("_log").appendKeyPart(server_guid), false);
                log_status.oldest_entry_pointer = oldestlogrow.Key.key_parts[oldestlogrow.Key.key_parts.Count - 1];

                yield return log_status;
            }

        }

        private void erasePendingLogEntries() {
            // TODO: erase all log entries newer than the commit_head
            // someday this might be an MVCC abort/rollback
        }

        private void workerFunc() {
            if (state != ReplState.active) {
                // be sure to pull us out of the connector registry!!
                ctx.connector.unregisterServer(ctx.server_guid);
            }

            // Make sure we try to stay connnected to all seeds so we can push writes to them
            // as fast as possible.
            pusher.scanSeeds();

                
            if (this.state == ReplState.init) {
                // we are initializing.... check our log state and see if we need a full rebuild                    

                // (1) see if we can resume from our commit_head pointers                    
                var our_log_status_dict = new Dictionary<string, LogStatus>();
                foreach (var ls in this.getStatusForLogs()) {
                    our_log_status_dict[ls.server_guid] = ls;
                }
                    
                ReplHandler srvr = pusher.getRandomSeed();
                foreach (var ls in srvr.getStatusForLogs()) {
                    if (!our_log_status_dict.ContainsKey(ls.server_guid)) {
                        // we are missing an entire log, we need a full rebuild!
                        this.state = ReplState.rebuild;
                        return;
                    }
                }

                // (2) erase our pending entries (those newer than commit heads)
                //     TODO: check them instead of erasing

                this.erasePendingLogEntries();

                // (3) replay from the commit heads

            } else if (this.state == ReplState.rebuild) {
                // we need a FULL rebuild
                Console.WriteLine("TODO: do full rebuild");                    
            } else {
                // we are just running!! 
                ctx.connector.registerServer(ctx.server_guid, this);
            }
        }

        private void workerThread() {
            int error_count = 0;
            while (true) {
                try {
                    workerFunc();
                } catch (Exception e) {
                    error_count++;
                    Console.WriteLine("Server ({0}) exception {1}:\n{2}",
                        ctx.server_guid, error_count, e.ToString());
                    if (error_count > 5) {
                        Console.WriteLine("too many exceptions, shutting down");
                        this.state = ReplState.shutdown;
                        return;
                    }
                }
                Thread.Sleep(1000);
            }
        }


        public static ReplHandler InitFresh(LayerManager db, ServerContext ctx) {
            // init fresh

            // record our instance ID
            db.setValue(new RecordKey().appendParsedKey(ctx.prefix_hack)
                .appendKeyPart("_config")
                .appendKeyPart("MY-SERVER-ID"),
                RecordUpdate.WithPayload(ctx.server_guid));

            // create and record a new instance ID
            db.setValue(new RecordKey().appendParsedKey(ctx.prefix_hack)
                .appendKeyPart("_config")
                .appendKeyPart("DATA-INSTANCE-ID"),
                RecordUpdate.WithPayload(Lsd.numberToLsd(ReplHandler.myrnd.Next(), 15)));

            ReplHandler repl = new ReplHandler(db, ctx);

            repl.state = ReplState.active; // TODO: is this the right way to become active?            
            return repl;

        }

        public static ReplHandler InitResume(LayerManager db, ServerContext ctx) {
            ReplHandler repl = new ReplHandler(db, ctx);
            return repl;
        }

        public static ReplHandler InitJoin(LayerManager db, ServerContext ctx, string seed_name) {

            // connect to the other server, get his instance id, exchange seeds
            ReplHandler seed = ctx.connector.getServerHandle(seed_name);
            ReplHandler.JoinInfo join_info = seed.requestToJoin(ctx.server_guid);

            // record the join result
            db.setValue(new RecordKey().appendParsedKey(ctx.prefix_hack)
                .appendKeyPart("_config")
                .appendKeyPart("DATA-INSTANCE-ID"),
                RecordUpdate.WithPayload(join_info.data_instance_id));

            foreach (var seed_server in join_info.seed_servers) {
                db.setValue(new RecordKey().appendParsedKey(ctx.prefix_hack)
                    .appendKeyPart("_config")
                    .appendKeyPart("seeds")
                    .appendKeyPart(seed_server),
                    RecordUpdate.WithPayload(""));                
            }

            Console.WriteLine("InitJoin: server ({0}) joining seeds ({1})",
                ctx.server_guid, String.Join(",", join_info.seed_servers));
                                  
            ReplHandler repl = new ReplHandler(db, ctx);
            return repl;
        }

     
        public JoinInfo requestToJoin(string server_guid) {
            // (1) record his guid
            db.setValue(new RecordKey().appendParsedKey(ctx.prefix_hack)
                .appendKeyPart("_config").appendKeyPart("seeds").appendKeyPart(server_guid),
                RecordUpdate.WithPayload(""));

            // (2) send him our instance ID and a list of seeds
            var ji = new JoinInfo();
            ji.data_instance_id = this.data_instance_id;

            ji.seed_servers = new List<string>();
            var seed_key_prefix = new RecordKey().appendParsedKey(ctx.prefix_hack)
                .appendKeyPart("_config")
                .appendKeyPart("seeds");            
            foreach (var row in db.scanForward(new ScanRange<RecordKey>(seed_key_prefix,RecordKey.AfterPrefix(seed_key_prefix),null))) {
                string sname = row.Key.key_parts[row.Key.key_parts.Count - 1];
                ji.seed_servers.Add(sname);
            }
            // add ourself to the seed list! 
            if (!ji.seed_servers.Contains(this.ctx.server_guid)) {
                ji.seed_servers.Add(this.ctx.server_guid); 
            }
            return ji;
        }

        public class JoinInfo {
            public string data_instance_id;
            public List<string> seed_servers;
        }

        public class ReplPusher {
            HashSet<string> servers;
            ReplHandler myhandler;
            public ReplPusher(ReplHandler handler) {
                servers = new HashSet<string>();
                myhandler = handler;
            }
            public void addServer(string server_guid) {
                if (server_guid.CompareTo(myhandler.ctx.server_guid) == 0) {
                    // we can't add ourself!!
                    return;
                }
                servers.Add(server_guid);
                Console.WriteLine("Server {0} pusher added seed {1}",
                    myhandler.ctx.server_guid, server_guid);
            }
            public ReplHandler getRandomSeed() {
                List<ReplHandler> available_servers = new List<ReplHandler>();
                this.scanSeeds();
                foreach (var server_guid in servers) {
                    try {
                        available_servers.Add(myhandler.ctx.connector.getServerHandle(server_guid));
                    } catch (KeyNotFoundException) {
                        Console.WriteLine("getRandomSeed: server {0} not available", server_guid);
                    }
                }
                if (available_servers.Count == 0) {
                    throw new KeyNotFoundException("getRandomSeed: no servers avaialble");
                }
                ReplHandler[] srvr_array = available_servers.ToArray();
                int pick = myhandler.rnd.Next(available_servers.Count);
                return available_servers[pick];
            }

            public void scanSeeds() {
                Console.WriteLine("** seed scan");
                var seed_key_prefix = new RecordKey()
                    .appendParsedKey(myhandler.ctx.prefix_hack)
                    .appendKeyPart("_config")
                    .appendKeyPart("seeds");
                foreach (var row in myhandler.db.scanForward(
                    new ScanRange<RecordKey>(seed_key_prefix, 
                        RecordKey.AfterPrefix(seed_key_prefix), null))) {
                    string sname = row.Key.key_parts[row.Key.key_parts.Count - 1];

                    Console.WriteLine("** seed scan row: {0}", row);
                    if (!servers.Contains(sname))
                        try {
                            ReplHandler srvr = myhandler.ctx.connector.getServerHandle(sname);
                            this.addServer(sname);
                            Console.WriteLine("Server {0} pusher, added seed {1}", myhandler.ctx.server_guid,
                                sname);
                        } catch (KeyNotFoundException) {
                        }

                }
            }

            public void pushNewLogEntry(byte[] logstamp, RecordUpdate logdata) {
                foreach (var server_guid in servers) {
                    try {
                        ReplHandler srvr = myhandler.ctx.connector.getServerHandle(server_guid);
                        srvr.applyLogEntry(myhandler.ctx.server_guid, logstamp, logdata);
                    } catch (KeyNotFoundException) {
                        Console.WriteLine("couldn't push to server: " + server_guid);
                    }
                }
            }
        }
    

        public void applyLogEntry(string from_server_guid, byte[] logstamp, RecordUpdate logdata) {
            // (0) unpack the data
            BlockAccessor ba = new BlockAccessor(logdata.data);
            ISegmentBlockDecoder decoder = new SegmentBlockBasicDecoder(ba);
                                               
            // (1) add it to our copy of that server's log
            RecordKey logkey = new RecordKey()
            .appendParsedKey(ctx.prefix_hack)
            .appendKeyPart("_logs")
            .appendKeyPart(from_server_guid)
            .appendKeyPart(logstamp);

            db.setValue(logkey, logdata);

            // (2) add it to the database

            foreach (var kvp in decoder.sortedWalk()) {
                RecordKey local_data_key = new RecordKey()
                    .appendParsedKey(ctx.prefix_hack)
                    .appendKeyPart("_data");
                foreach (var part in kvp.Key.key_parts) {
                    local_data_key.appendKeyPart(part);
                }
                db.setValue(local_data_key, kvp.Value);
            }

            

        }

        public void setValue(RecordKey skey, RecordUpdate supdate) {
            // (1) write our repl log entry

            DateTime now = DateTime.Now;
            long timestamp = (now.Ticks * 100000) + now.Millisecond + rnd.Next(100);

            byte[] logstamp = Lsd.numberToLsd(timestamp, 35);
            RecordKey logkey = new RecordKey()
                .appendParsedKey(ctx.prefix_hack)
                .appendKeyPart("_logs")
                .appendKeyPart(ctx.server_guid)
                .appendKeyPart(logstamp);

            // (1.1) pack the key/value together into the log entry
            byte[] packed_update;
            {
                MemoryStream writer = new MemoryStream();
                // TODO: this seems like a really inefficient way to write out a key
                ISegmentBlockEncoder encoder = new SegmentBlockBasicEncoder();
                encoder.setStream(writer);
                encoder.add(skey, supdate);
                encoder.flush();
                packed_update = writer.ToArray();
            }
            RecordUpdate logupdate = RecordUpdate.WithPayload(packed_update);


            Console.WriteLine("writing log entry: {0} -> [ {1} = {2} ]",
                logkey, skey, supdate);
            db.setValue(logkey, logupdate);

            // (2) write the record key
            Console.WriteLine("writing data entry: {0} = {1}",
                skey, supdate);
            RecordKey private_record_key = new RecordKey()
                .appendParsedKey(ctx.prefix_hack)
                .appendKeyPart("_data");
            foreach (var part in skey.key_parts) {
                private_record_key.appendKeyPart(part);
            }
            db.setValue(private_record_key, supdate);

            // (3) trigger the repl notifier that there is a new entry to push
            pusher.pushNewLogEntry(logstamp, logupdate);
        }
        
        public void setValueParsed(string skey, string svalue) {
            RecordKey key = new RecordKey();
            key.appendParsedKey(skey);
            RecordUpdate update = RecordUpdate.WithPayload(svalue);

            this.setValue(key, update);
        }
    }

}