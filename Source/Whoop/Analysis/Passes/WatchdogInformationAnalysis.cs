﻿// ===-----------------------------------------------------------------------==//
//
//                 Whoop - a Verifier for Device Drivers
//
//  Copyright (c) 2013-2014 Pantazis Deligiannis (p.deligiannis@imperial.ac.uk)
//
//  This file is distributed under the Microsoft Public License.  See
//  LICENSE.TXT for details.
//
// ===----------------------------------------------------------------------===//

using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Linq;

using Microsoft.Boogie;
using Microsoft.Basetypes;

using Whoop.Analysis;
using Whoop.Domain.Drivers;
using Whoop.Regions;

namespace Whoop.Analysis
{
  internal class WatchdogInformationAnalysis : IWatchdogInformationAnalysis
  {
    protected AnalysisContext AC;
    protected EntryPoint EP;
    protected ExecutionTimer Timer;

    public WatchdogInformationAnalysis(AnalysisContext ac, EntryPoint ep)
    {
      Contract.Requires(ac != null && ep != null);
      this.AC = ac;
      this.EP = ep;

      InstrumentationRegion.MatchedAccesses.Add(this.EP, new List<HashSet<string>>());
    }

    public void Run()
    {
      if (WhoopCommandLineOptions.Get().MeasurePassExecutionTime)
      {
        this.Timer = new ExecutionTimer();
        this.Timer.Start();
      }

      this.AnalyseInstrumentationRegions();
      this.CleanUpRegions();

      if (WhoopCommandLineOptions.Get().MeasurePassExecutionTime)
      {
        this.Timer.Stop();
        Console.WriteLine(" |  |------ [WatchdogInformationAnalysis] {0}", this.Timer.Result());
      }
    }

    #region watchdog information analysis

    private void AnalyseInstrumentationRegions()
    {
      var fixpoint = true;
      foreach (var region in this.AC.InstrumentationRegions)
      {
        fixpoint = this.AnalyseAccessesInRegion(region) && fixpoint;
      }

      if (!fixpoint)
      {
        this.AnalyseInstrumentationRegions();
      }
    }

    private bool AnalyseAccessesInRegion(InstrumentationRegion region)
    {
      if (region.IsResourceAnalysisDone)
        return true;

      int preCount = 0;
      int afterCount = 0;

      foreach (var r in region.GetResourceAccesses())
      {
        preCount = preCount + r.Value.Count;
      }

      int numberOfCalls = 0;
      int numberOfNonCheckedCalls = 0;

      if (!region.IsLocalResourceAnalysisDone)
      {
        foreach (var block in region.Implementation().Blocks)
        {
          for (int idx = 0; idx < block.Cmds.Count; idx++)
          {
            if (!(block.Cmds[idx] is CallCmd))
              continue;

            var call = block.Cmds[idx] as CallCmd;
            bool isInstrumentedCall = false;
            string resource = null;
            string accessType = null;

            if (idx + 1 < block.Cmds.Count && block.Cmds[idx + 1] is AssumeCmd)
            {
              isInstrumentedCall = QKeyValue.FindStringAttribute((block.Cmds[idx + 1]
                as AssumeCmd).Attributes, "captureState") != null;
              resource = QKeyValue.FindStringAttribute((block.Cmds[idx + 1]
                as AssumeCmd).Attributes, "resource");
              accessType = QKeyValue.FindStringAttribute((block.Cmds[idx + 1]
                as AssumeCmd).Attributes, "access");
            }

            if (isInstrumentedCall && resource != null && accessType != null)
            {
              var ptrExpr = DataFlowAnalyser.ComputeRootPointer(region.Implementation(), block.Label, call.Ins[0]);
              if (ptrExpr.ToString().Substring(0, 2).Equals("$p"))
                ptrExpr = null;
              region.TryAddLocalResourceAccess(resource, ptrExpr);
            }
          }
        }

        region.IsLocalResourceAnalysisDone = true;
      }

      foreach (var block in region.Implementation().Blocks)
      {
        for (int idx = 0; idx < block.Cmds.Count; idx++)
        {
          if (!(block.Cmds[idx] is CallCmd))
            continue;

          var call = block.Cmds[idx] as CallCmd;
          bool isInstrumentedCall = false;

          if (idx + 1 < block.Cmds.Count && block.Cmds[idx + 1] is AssumeCmd)
          {
            isInstrumentedCall = QKeyValue.FindStringAttribute((block.Cmds[idx + 1]
              as AssumeCmd).Attributes, "captureState") != null;
          }

          if (!isInstrumentedCall)
          {
            numberOfCalls++;

            var calleeRegion = this.AC.InstrumentationRegions.Find(val =>
              val.Implementation().Name.Equals(call.callee));
            if (calleeRegion == null)
            {
              numberOfNonCheckedCalls++;
              continue;
            }

            if (calleeRegion.GetResourceAccesses() == null)
              continue;
            if (calleeRegion.GetResourceAccesses().Count == 0 &&
                calleeRegion.IsResourceAnalysisDone)
            {
              numberOfNonCheckedCalls++;
              continue;
            }

            if (!region.CallInformation.ContainsKey(call))
            {
              region.CallInformation.Add(call, new Dictionary<int, Tuple<Expr, Expr>>());

              for (int i = 0; i < call.Ins.Count; i++)
              {
                var ptrExpr = DataFlowAnalyser.ComputeRootPointer(region.Implementation(),
                  block.Label, call.Ins[i]);
                if (ptrExpr.ToString().Length > 2 && ptrExpr.ToString().Substring(0, 2).Equals("$p"))
                  ptrExpr = null;

                var id = calleeRegion.Implementation().InParams[i];
                region.CallInformation[call].Add(i, new Tuple<Expr, Expr>(ptrExpr, new IdentifierExpr(id.tok, id)));
              }
            }

            foreach (var r in calleeRegion.GetResourceAccesses())
            {
              foreach (var a in r.Value)
              {
                int index;
                if (a is NAryExpr)
                  index = this.TryGetArgumentIndex(call, calleeRegion, (a as NAryExpr).Args[0]);
                else
                  index = this.TryGetArgumentIndex(call, calleeRegion, a);
                if (index < 0)
                  continue;

                var calleeExpr = region.CallInformation[call][index];
                var computedExpr = this.ComputeExpr(calleeExpr.Item1, a);
                region.TryAddResourceAccess(r.Key, computedExpr);
              }
            }

//            foreach (var pair in region.GetResourceAccesses())
//            {
//              foreach (var access in pair.Value)
//              {
//                foreach (var index in region.CallInformation[call].Keys)
//                {
//                  var calleeExpr = region.CallInformation[call][index];
//                  var mappedExpr = this.ComputeMappedExpr(access, calleeExpr.Item1, calleeExpr.Item2);
//                  if (mappedExpr != null &&
//                    calleeRegion.TryAddExternalResourceAccesses(pair.Key, mappedExpr))
//                  {
//                    this.CacheMatchedAccesses(pair.Key, access, mappedExpr);
//                    afterCount++;
//                  }
//                }
//              }
//            }
          }
        }
      }

      if (region.GetResourceAccesses().Count == 0 &&
        numberOfCalls == numberOfNonCheckedCalls)
      {
        this.CleanUpRegion(region);
        region.IsResourceAnalysisDone = true;
        return false;
      }

      foreach (var r in region.GetResourceAccesses())
      {
        afterCount = afterCount + r.Value.Count;
      }

      if (preCount != afterCount) return false;
      else return true;
    }

    private void CleanUpRegions()
    {
//      foreach (var region in this.AC.InstrumentationRegions)
//      {
//        if (region.ResourceAccesses.Count > 0)
//          continue;
//        Console.WriteLine("region: " + region.Name() + " " + region.ResourcesWithUnidentifiedAccesses.Count);
//        if (region.ResourcesWithUnidentifiedAccesses.Count > 0)
//          continue;
//
//        this.CleanUpInstrumentationRegion(region);
//      }
    }

    #endregion

    #region helper functions

    private int TryGetArgumentIndex(CallCmd call, IRegion callRegion, Expr access)
    {
      int index = -1;
      for (int idx = 0; idx < callRegion.Implementation().InParams.Count; idx++)
      {
        if (callRegion.Implementation().InParams[idx].ToString().Equals(access.ToString()))
        {
          index = idx;
          break;
        }
      }

      return index;
    }

    private Expr ComputeExpr(Expr ptrExpr, Expr access)
    {
      Expr result = null;

      if (access is NAryExpr)
      {
        var a = (access as NAryExpr).Args[1];
        if (ptrExpr is NAryExpr)
        {
          var ptr = ptrExpr as NAryExpr;
          if (!(ptr.Args[1] is LiteralExpr) || !(a is LiteralExpr))
            return result;

          int l = (ptr.Args[1] as LiteralExpr).asBigNum.ToInt;
          int r = (a as LiteralExpr).asBigNum.ToInt;

          if (ptr.Fun.FunctionName == "$add" || ptr.Fun.FunctionName == "+")
          {
            if ((access as NAryExpr).Fun.FunctionName == "$add" ||
              (access as NAryExpr).Fun.FunctionName == "+")
            {
              result = Expr.Add(ptr.Args[0], new LiteralExpr(Token.NoToken, BigNum.FromInt(l + r)));
            }
          }
        }
        else if (ptrExpr is IdentifierExpr)
        {
          if (this.AC.GetNumOfEntryPointRelatedFunctions(this.EP.Name) >
            WhoopCommandLineOptions.Get().EntryPointFunctionCallComplexity)
            return result;

          var ptr = ptrExpr as IdentifierExpr;
          if (!ptr.Type.IsInt)
            return result;

          if ((access as NAryExpr).Fun.FunctionName == "$add" ||
            (access as NAryExpr).Fun.FunctionName == "+")
          {
            result = Expr.Add(ptr, new LiteralExpr(Token.NoToken, (a as LiteralExpr).asBigNum));
          }
        }
      }
      else
      {
        result = ptrExpr;
      }

      return result;
    }

    private Expr ComputeMappedExpr(Expr localAccess, Expr ptrExpr, Expr calleeExpr)
    {
      Expr result = null;

      Expr ce = null;
      if (calleeExpr is NAryExpr)
        ce = (calleeExpr as NAryExpr).Args[0];
      else
        ce = calleeExpr;

      if (localAccess is NAryExpr && ptrExpr is NAryExpr)
      {
        var la = localAccess as NAryExpr;
        var pe = ptrExpr as NAryExpr;

        if (!la.Args[0].ToString().Equals(pe.Args[0].ToString()))
          return result;

        int l = (la.Args[1] as LiteralExpr).asBigNum.ToInt;
        int r = (pe.Args[1] as LiteralExpr).asBigNum.ToInt;

        if (l == r)
          result = ce;
        else if (l > r)
          result = Expr.Add(ce, new LiteralExpr(Token.NoToken, BigNum.FromInt(l - r)));
        // Not sure if the below condition is ever true.
        else if (l > r)
          result = Expr.Sub(ce, new LiteralExpr(Token.NoToken, BigNum.FromInt(r - l)));
      }
      else if (localAccess is NAryExpr && ptrExpr is IdentifierExpr)
      {
        var la = localAccess as NAryExpr;

        if (!la.Args[0].ToString().Equals(ptrExpr.ToString()))
          return result;

        int l = (la.Args[1] as LiteralExpr).asBigNum.ToInt;
        result = Expr.Add(ce, new LiteralExpr(Token.NoToken, BigNum.FromInt(l)));
      }
      else if (ptrExpr is NAryExpr)
      {
        var pe = ptrExpr as NAryExpr;

        if (!localAccess.ToString().Equals(pe.Args[0].ToString()))
          return result;

        int r = (pe.Args[1] as LiteralExpr).asBigNum.ToInt;


      }
      else if (ptrExpr is IdentifierExpr)
      {
        if (!localAccess.ToString().Equals(ptrExpr.ToString()))
          return result;
        result = ce;
      }

      return result;
    }

    private void CacheMatchedAccesses(string resource, Expr expr1, Expr expr2)
    {
      string str1 = expr1.ToString();
      string str2 = expr2.ToString();

      if (expr1 is IdentifierExpr)
        str1 = str1 + " + 0";
      if (expr2 is IdentifierExpr)
        str2 = str2 + " + 0";

      bool foundIt = false;
      foreach (var matchedSet in InstrumentationRegion.MatchedAccesses[this.EP])
      {
        if (matchedSet.Contains(str1) || matchedSet.Contains(str2))
        {
          matchedSet.Add(expr1.ToString());
          matchedSet.Add(expr2.ToString());
          foundIt = true;
        }
      }

      if (!foundIt)
      {
        InstrumentationRegion.MatchedAccesses[this.EP].Add(
          new HashSet<string> { expr1.ToString(), expr2.ToString() });
      }
    }

    private void CleanUpRegion(InstrumentationRegion region)
    {
      region.Implementation().Proc.Modifies.RemoveAll(val =>
        val.Name.Contains("_in_LS_$M.") ||
        val.Name.StartsWith("WRITTEN_$M.") ||
        val.Name.StartsWith("READ_$M."));
    }

    #endregion
  }
}
