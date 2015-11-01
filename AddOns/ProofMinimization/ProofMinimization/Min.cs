﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Boogie;
using System.Diagnostics;
using cba.Util;
using PersistentProgram = ProgTransformation.PersistentProgram;
using System.Text.RegularExpressions;
using btype = Microsoft.Boogie.Type;

namespace ProofMinimization
{
    // TODO: make this non-static
    static class Minimize
    {
        public static bool dbg = false;
        static readonly int TemplateCounterStart = 1000;
        public static int IterCnt = 0;
        public static int CacheHit = 0;
        public static bool usePerf = false;
        public static bool useSI = true;

        // file name -> Program
        static Dictionary<string, PersistentProgram> fileToProg = new Dictionary<string, PersistentProgram>();
        // template ID -> file -> {constants}
        static Dictionary<int, Dictionary<string, HashSet<string>>> templateMap = new Dictionary<int, Dictionary<string, HashSet<string>>>();
        // identifier string -> template ID 
        public static Dictionary<string, int> strToTemplate = new Dictionary<string, int>();
        public static Dictionary<int, string> templateToStr = new Dictionary<int, string>();
        // templates -> cost (lowest cost picked first)
        public static Dictionary<int, int> candidateToCost = new Dictionary<int, int>();

        // file -> constants to keep always
        static Dictionary<string, HashSet<string>> fileToKeepConstants = new Dictionary<string, HashSet<string>>();
        // file -> perf
        static Dictionary<string, int> fileToPerf = new Dictionary<string, int>();

        static void addTemplate(int templateId, string file, string constant)
        {
            if (!templateMap.ContainsKey(templateId)) templateMap.Add(templateId, new Dictionary<string, HashSet<string>>());
            if (!templateMap[templateId].ContainsKey(file)) templateMap[templateId].Add(file, new HashSet<string>());
            templateMap[templateId][file].Add(constant);
        }

        public static void ReadFiles(IEnumerable<string> files, HashSet<string> keepPatterns)
        {
            foreach (var f in files)
            {
                var program = BoogieUtil.ReadAndResolve(f);
                CheckRMT(program);

                fileToKeepConstants.Add(f, new HashSet<string>());

                // Add annotations
                foreach (var p in keepPatterns)
                {
                    var re = new Regex(p);
                    program.TopLevelDeclarations.OfType<Constant>()
                    .Where(c => QKeyValue.FindBoolAttribute(c.Attributes, "existential") && re.IsMatch(c.Name))
                    .Iter(c =>
                        {
                            c.AddAttribute(Driver.MustKeepAttr);
                            fileToKeepConstants[f].Add(c.Name);
                        });
                }

                // Prune, according to annotations
                DropConstants(program, new HashSet<string>(
                    program.TopLevelDeclarations.OfType<Constant>()
                    .Where(c => QKeyValue.FindBoolAttribute(c.Attributes, Driver.DropAttr))
                    .Select(c => c.Name)));

                var allconstants = new Dictionary<string, Constant>();
                program.TopLevelDeclarations.OfType<Constant>()
                    .Where(c => QKeyValue.FindBoolAttribute(c.Attributes, "existential"))
                    .Iter(c => allconstants.Add(c.Name, c));

                // Normalize expressions
                program.TopLevelDeclarations.OfType<Implementation>()
                    .Iter(impl => impl.Proc.Ensures.Iter(ens => SimplifyExpr.SimplifyEnsures(ens, allconstants)));

                // Remove constants that don't hold -- optimization
                HashSet<string> consts = new HashSet<string>(allconstants.Keys);
                HashSet<string> assignment;
                int perf = 0;

                if (Driver.InitBound > 0)
                    perf = Driver.InitBound;

                var ret = PruneAndRun(new PersistentProgram(program), consts, out assignment, ref perf);

                if (ret != BoogieVerify.ReturnStatus.OK)
                {
                    Console.WriteLine("Warning: Program {0} doesn't have an initial inductive proof", f);
                    continue;
                }

                // anything not in assignment can be dropped
                DropConstants(program, consts.Difference(assignment));
                consts.Difference(assignment).Iter(s => allconstants.Remove(s));
                fileToKeepConstants[f].IntersectWith(assignment);

                Console.WriteLine("File {0} defines {1} constants ({2} dropped)", f, assignment.Count, consts.Count - assignment.Count);

                // initial perf
                fileToPerf.Add(f, perf);

                // create template map
                foreach (var impl in program.TopLevelDeclarations.OfType<Implementation>())
                {
                    foreach (var ens in impl.Proc.Ensures.Where(e => !e.Free))
                    {
                        string constantName = null;
                        Expr expr = null;

                        var match = Microsoft.Boogie.Houdini.Houdini.GetCandidateWithoutConstant(
                            ens.Condition, allconstants.Keys, out constantName, out expr);

                        if (!match) continue;
                        if (fileToKeepConstants[f].Contains(constantName)) continue;

                        var templateId = QKeyValue.FindIntAttribute(ens.Attributes, "template", -1);
                        if (templateId != -1)
                        {
                            addTemplate(templateId, f, constantName);
                            if (!strToTemplate.ContainsKey(SimplifyExpr.ExprToTemplate(expr)))
                            {
                                strToTemplate.Add(SimplifyExpr.ExprToTemplate(expr), templateId);
                            }
                        }
                        else
                        {
                            var temp = SimplifyExpr.ExprToTemplate(expr);
                            if (strToTemplate.ContainsKey(temp))
                            {
                                // template for it exists
                                addTemplate(strToTemplate[temp], f, constantName);
                            }
                            else
                            {
                                // create new template
                                strToTemplate.Add(temp, TemplateCounterStart + strToTemplate.Count);
                                addTemplate(strToTemplate[temp], f, constantName);
                            }
                        }
                    }

                }

                // stash the program
                fileToProg.Add(f, new PersistentProgram(program));
            }

            templateToStr = new Dictionary<int, string>();
            strToTemplate.Iter(tup => templateToStr.Add(tup.Value, tup.Key));

            Console.WriteLine("Found {0} templates", templateMap.Count);

            if (dbg)
            {
                foreach (var tup in templateMap)
                {
                    Console.WriteLine("Template {0} :: {1}", tup.Key, templateToStr[tup.Key]);
                    foreach (var tup2 in tup.Value)
                    {
                        Console.WriteLine("  File {0}", tup2.Key);
                        tup2.Value.Iter(c => Console.WriteLine("    Candidate {0}", c));
                    }
                }
            }
        }

        // Minimize
        public static HashSet<int> FindMin(HashSet<int> templatesAlreadyDropped, out Dictionary<int, int> templateToPerfDelta)
        {
            templateToPerfDelta = new Dictionary<int, int>();

            var iter = 1;
            var tokeep = new HashSet<int>();
            var templates = new HashSet<int>(templateMap.Keys);
            var sw = new Stopwatch();
            
            sw.Start();

            templates.ExceptWith(templatesAlreadyDropped);
            templates.Where(t => !candidateToCost.ContainsKey(t))
                .Iter(t => candidateToCost.Add(t, 0));

            while (templates.Count != 0)
            {
                Console.WriteLine("------ ITER {0} -------", iter++);

                // Drop one and re-run
                var c = PickRandom(templates);

                templates.Remove(c);

                Console.WriteLine("  >> Trying {0} :: {1}", c, templateToStr[c]);

                Dictionary<string, int> perf;
                HashSet<string> filesVerified;
                string fileFailed;

                var rt = PruneAndRun(templates.Union(tokeep), out perf, out filesVerified, out fileFailed);

                if (rt == BoogieVerify.ReturnStatus.OK)
                {
                    // dropping was fine
                    Console.WriteLine("  >> Files verified: {0}", filesVerified.Concat(" "));
                    Console.WriteLine("  >> Dropping it");

                    // TODO: maintain information of extra constants that are
                    // no longer provable, and prune templateMap accordingly

                    // Extra information
                    var delta = 0;
                    perf.Iter(tup => delta += (tup.Value - fileToPerf[tup.Key]));
                    templateToPerfDelta[c] = delta;

                    // Update perf stats
                    perf.Iter(tup => fileToPerf[tup.Key] = tup.Value);
                }
                else
                {
                    Debug.Assert(fileFailed != null);
                    Console.WriteLine("  >> Files verified: {0}", filesVerified.Concat(" "));
                    Console.WriteLine("  >> File failed: {0}", fileFailed);
                    Console.WriteLine("  >> Cannot drop");
                    tokeep.Add(c);
                }

                //Log(iter);
                Console.WriteLine("Time elapsed: {0} sec", sw.Elapsed.TotalSeconds.ToString("F2"));
            }

            return tokeep;
        }

        static int NewConstCounter = 0;

        // minimize disjunctions in the templates
        // Returns true if new constants were created
        // Add new templates (and existing ones for which new constants were created) to newTemplates
        public static bool PruneDisjuncts(int template, ref HashSet<int> newTemplates)
        {
            Console.WriteLine("Breaking down template {0}: {1}", template, templateToStr[template]);
            var newConstantsCreated = false;

            var files = new HashSet<string>(fileToProg.Keys);
            foreach (var file in files)
            {
                if (!templateMap[template].ContainsKey(file))
                    continue;
                var constants = templateMap[template][file].Difference(fileToKeepConstants[file]);
                if (constants.Count == 0) continue;

                var program = fileToProg[file].getProgram();

                var newconstants = new List<Constant>();
                foreach (var impl in program.TopLevelDeclarations.OfType<Implementation>())
                {
                    var newEnsures = new List<Ensures>();
                    foreach (var ens in impl.Proc.Ensures.Where(e => !e.Free))
                    {
                        string constantName = null;
                        Expr expr = null;

                        var match = Microsoft.Boogie.Houdini.Houdini.GetCandidateWithoutConstant(
                            ens.Condition, constants, out constantName, out expr);

                        if (!match) continue;

                        var disjuncts = SimplifyExpr.GetExprDisjuncts(expr);
                        if (disjuncts.Count == 1) continue;
                        var pow = SimplifyExpr.GetPowSetDisjunctions(disjuncts);
                        foreach (var d in pow)
                        {
                            var conjuncts = SimplifyExpr.GetExprConjunctions(d);
                            foreach (var c in conjuncts)
                            {
                                var nc = new Constant(Token.NoToken, new TypedIdent(Token.NoToken, "PMnewConst" + (NewConstCounter++),
                                    btype.Bool), false);
                                nc.AddAttribute("existential");
                                newconstants.Add(nc);
                                templateMap[template][file].Add(nc.Name);
                                //Console.WriteLine("  New constant created: {0}", nc.Name);
                                newConstantsCreated = true;

                                var e = Expr.Imp(Expr.Ident(nc), c);
                                newEnsures.Add(new Ensures(false, e));

                                // Add template
                                var temp = SimplifyExpr.ExprToTemplate(c);
                                if (strToTemplate.ContainsKey(temp))
                                {
                                    // template for it exists
                                    addTemplate(strToTemplate[temp], file, nc.Name);                                    
                                }
                                else
                                {
                                    // create new template
                                    int v = TemplateCounterStart + strToTemplate.Count;
                                    strToTemplate.Add(temp, v);
                                    templateToStr.Add(v, temp);
                                    addTemplate(strToTemplate[temp], file, nc.Name);                                                           
                                }

                                if(!candidateToCost.ContainsKey(strToTemplate[temp]))
                                    candidateToCost.Add(strToTemplate[temp], conjuncts.Count > 1 ? disjuncts.Count : disjuncts.Count - SimplifyExpr.GetExprDisjuncts(d).Count);
                                Console.WriteLine("  Template revived or created {0} :: {1} (cost = {2})", strToTemplate[temp], temp, candidateToCost[strToTemplate[temp]]);
                                newTemplates.Add(strToTemplate[temp]);
                            }
                        }
                    }
                    impl.Proc.Ensures.AddRange(newEnsures);
                }
                program.AddTopLevelDeclarations(newconstants);

                fileToProg[file] = new PersistentProgram(program);
            }
            return newConstantsCreated;
        }


        static Dictionary<PersistentProgram, Tuple<HashSet<string>, int>> PreviousRun
            = new Dictionary<PersistentProgram, Tuple<HashSet<string>, int>>();

        // Prune away non-candidates, verify using the rest
        static BoogieVerify.ReturnStatus PruneAndRun(HashSet<int> candidateTemplates, out Dictionary<string, int> perf, out HashSet<string> filesVerified, out string fileFailed)
        {
            perf = new Dictionary<string, int>();

            filesVerified = new HashSet<string>();
            fileFailed = null;

            foreach (var tup in fileToProg)
            {
                var file = tup.Key;

                var program = tup.Value.getProgram();
                program.Typecheck();

                // all constants
                var allconstants = new HashSet<string>(
                    program.TopLevelDeclarations.OfType<Constant>()
                    .Where(c => QKeyValue.FindBoolAttribute(c.Attributes, "existential"))
                    .Select(c => c.Name));

                // candidate constants
                var candidates = new HashSet<string>();
                candidateTemplates.Where(t => templateMap[t].ContainsKey(file))
                    .Iter(t => candidates.UnionWith(templateMap[t][file]));

                // to keep
                var keep = fileToKeepConstants[file].Intersection(allconstants);

                var cache_try = new HashSet<string>(candidates.Concat(keep));

                IterCnt++;

                if (PreviousRun.ContainsKey(tup.Value))
                {
                    var cache_entry = PreviousRun[tup.Value];
                    if (cache_try.Count == cache_entry.Item1.Count && 
                        cache_entry.Item1.SetEquals(cache_try))
                    {
                        CacheHit++;
                        filesVerified.Add(file);
                        perf[file] = cache_entry.Item2;
                        continue;
                    }
                }

                // drop
                DropConstants(program, allconstants.Difference(candidates.Union(keep)));

                // Run Houdini
                var assignment = CoreLib.HoudiniInlining.RunHoudini(program, true);
                //Console.WriteLine("  >> Contracts: {0}", assignment.Count);

                // Read the program again, add contracts
                program = tup.Value.getProgram();
                program.Typecheck();

                // Enforce the assignment back into the program
                CoreLib.HoudiniInlining.InstrumentHoudiniAssignment(program, assignment);

                // Run SI
                var err = new List<BoogieErrorTrace>();

                // Set bound
                BoogieVerify.options.maxInlinedBound = 0;
                if (fileToPerf[file] != 0)
                    BoogieVerify.options.maxInlinedBound = PerfMetric(fileToPerf[file]);

                var rstatus = BoogieVerify.Verify(program, out err, true);
                if (dbg)
                {
                    Console.WriteLine(string.Format("  >> Procedures Inlined: {0} / {1}", BoogieVerify.CallTreeSize, BoogieVerify.options.maxInlinedBound));
                    Console.WriteLine(string.Format("Boogie verification time: {0} s", BoogieVerify.verificationTime.TotalSeconds.ToString("F2")));
                }

                var procs_inlined = BoogieVerify.CallTreeSize + 1;
                BoogieVerify.options.CallTree = new HashSet<string>();
                BoogieVerify.CallTreeSize = 0;
                BoogieVerify.verificationTime = TimeSpan.Zero;

                if (rstatus != BoogieVerify.ReturnStatus.OK)
                {
                    fileFailed = file;
                    return BoogieVerify.ReturnStatus.NOK;
                }

                perf[file] = procs_inlined;
                filesVerified.Add(file);

                // cache
                if (rstatus == BoogieVerify.ReturnStatus.OK)
                {
                    PreviousRun[tup.Value] = Tuple.Create(cache_try, procs_inlined);
                }
            }

            return BoogieVerify.ReturnStatus.OK;
        }

        // Prune away non-candidates, verify using the rest
        static BoogieVerify.ReturnStatus PruneAndRun(PersistentProgram inp, HashSet<string> candidates, out HashSet<string> assignment, ref int inlined)
        {
            var program = inp.getProgram();
            program.Typecheck();

            program =  BoogieUtil.ReResolve(program);

            // Remove non-candidates
            CoreLib.HoudiniInlining.InstrumentHoudiniAssignment(program, candidates, true);
            program.RemoveTopLevelDeclarations(decl => (decl is Constant) && QKeyValue.FindBoolAttribute(decl.Attributes, "existential") &&
                !candidates.Contains((decl as Constant).Name));

            //BoogieUtil.PrintProgram(program, "hi_query" + IterCnt + ".bpl");

            // Run Houdini
            assignment = CoreLib.HoudiniInlining.RunHoudini(program, true);
            //Console.WriteLine("  >> Contracts: {0}", assignment.Count);

            // Read the program again, add contracts
            program = inp.getProgram();
            program.Typecheck();

            CoreLib.HoudiniInlining.InstrumentHoudiniAssignment(program, assignment);
            //BoogieUtil.PrintProgram(program, "si_query" + IterCnt + ".bpl");

            // Run SI
            var err = new List<BoogieErrorTrace>();

            // Set bound
            BoogieVerify.options.maxInlinedBound = 0;
            if (inlined != 0)
                BoogieVerify.options.maxInlinedBound = PerfMetric(inlined);

            var rstatus = BoogieVerify.Verify(program, out err, true);
            //Console.WriteLine(string.Format("  >> Procedures Inlined: {0}", BoogieVerify.CallTreeSize));
            //Console.WriteLine(string.Format("Boogie verification time: {0} s", BoogieVerify.verificationTime.TotalSeconds.ToString("F2")));

            inlined = BoogieVerify.CallTreeSize + 1;
            BoogieVerify.options.CallTree = new HashSet<string>();
            BoogieVerify.CallTreeSize = 0;
            BoogieVerify.verificationTime = TimeSpan.Zero;

            return rstatus;
        }

        static int PerfMetric(int n)
        {
            if (!usePerf) return Int32.MaxValue;
            if (!useSI) return (n+1);
            if (n < 50) return (n + 100);
            return 2 * n;
        }

        static Random rand = null;

        // pick one with max cost randomly
        static int PickRandom(HashSet<int> set)
        {
            Debug.Assert(set.Count != 0);

            if (rand == null)
            {
                rand = new Random((int)DateTime.Now.Ticks);
            }

            var min = set.Select(c => candidateToCost[c]).Min();
            var selected = set.Where(s => candidateToCost[s] == min);
            //var selected = set;

            var ind = rand.Next(selected.Count());
            return selected.ToList()[ind];
        }

        // Prune away some constants from the program
        static void DropConstants(Program program, HashSet<string> drop)
        {
            
            var constants = new HashSet<string>(
                program.TopLevelDeclarations.OfType<Constant>()
                .Select(c => c.Name));

            var dontdrop = constants.Difference(drop);

            CoreLib.HoudiniInlining.InstrumentHoudiniAssignment(program, dontdrop, true);
            program.RemoveTopLevelDeclarations(c => c is Constant && drop.Contains((c as Constant).Name));
        }

        // Input program must be an RMT query
        static void CheckRMT(Program program)
        {
            if (program.TopLevelDeclarations.OfType<Implementation>()
                .Any(impl => impl.Blocks
                    .Any(blk => blk.Cmds
                        .Any(c => c is AssertCmd && !BoogieUtil.isAssertTrue(c)))))
                throw new Exception("Input program cannot have an assertion");

            var ep = program.TopLevelDeclarations.OfType<Implementation>()
                .Where(impl => QKeyValue.FindBoolAttribute(impl.Attributes, "entrypoint"))
                .FirstOrDefault();

            if (ep == null)
                throw new Exception("Entrypoint not found");
        }
    }
}