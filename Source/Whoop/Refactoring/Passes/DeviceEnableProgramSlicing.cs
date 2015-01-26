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

namespace Whoop.Refactoring
{
  internal class DeviceEnableProgramSlicing : IDeviceEnableProgramSlicing
  {
    private AnalysisContext AC;
    private EntryPoint EP;
    private ExecutionTimer Timer;

    private InstrumentationRegion ChangingRegion;
    private HashSet<InstrumentationRegion> SlicedRegions;

    public DeviceEnableProgramSlicing(AnalysisContext ac, EntryPoint ep)
    {
      Contract.Requires(ac != null && ep != null);
      this.AC = ac;
      this.EP = ep;

      this.ChangingRegion = this.AC.InstrumentationRegions.Find(val => val.IsChangingDeviceRegistration);
      this.SlicedRegions = new HashSet<InstrumentationRegion>();
    }

    public void Run()
    {
      if (WhoopCommandLineOptions.Get().MeasurePassExecutionTime)
      {
        this.Timer = new ExecutionTimer();
        this.Timer.Start();
      }

      foreach (var region in this.AC.InstrumentationRegions)
      {
        this.SimplifyCallsInRegion(region);
      }

      foreach (var region in this.SlicedRegions)
      {
        this.SliceRegion(region);
      }

      this.SimplifyAccessesInChangingRegion();
      var predecessors = this.EP.CallGraph.NestedPredecessors(this.ChangingRegion);
      var successors = this.EP.CallGraph.NestedSuccessors(this.ChangingRegion);
      predecessors.RemoveWhere(val => successors.Contains(val));
      this.SimplifyAccessesInPredecessors(predecessors);

      if (WhoopCommandLineOptions.Get().MeasurePassExecutionTime)
      {
        this.Timer.Stop();
        Console.WriteLine(" |  |------ [DeviceEnableProgramSlicing] {0}", this.Timer.Result());
      }
    }

    #region net program slicing functions

    private void SimplifyCallsInRegion(InstrumentationRegion region)
    {
      foreach (var call in region.Cmds().OfType<CallCmd>())
      {
        var calleeRegion = this.AC.InstrumentationRegions.Find(val =>
          val.Implementation().Name.Equals(call.callee));
        if (calleeRegion == null)
          continue;
        if (calleeRegion.IsDeviceRegistered || calleeRegion.IsChangingDeviceRegistration)
          continue;

        call.callee = "_NO_OP_$" + this.EP.Name;
        call.Ins.Clear();
        call.Outs.Clear();

        this.SlicedRegions.Add(calleeRegion);
      }
    }

    private void SimplifyAccessesInChangingRegion()
    {
      var blockGraph = Graph<Block>.BuildBlockGraph(this.ChangingRegion.Blocks());

      Block devBlock = null;
      CallCmd devCall = null;

      foreach (var block in this.ChangingRegion.Blocks())
      {
        foreach (var call in block.Cmds.OfType<CallCmd>())
        {
          if (devBlock == null && call.callee.StartsWith("_REGISTER_DEVICE_"))
          {
            devBlock = block;
            devCall = call;
            break;
          }
        }

        if (devBlock != null)
          break;
      }

      this.SimplifyAccessInBlocks(blockGraph, devBlock, devCall);
    }

    private void SimplifyAccessesInPredecessors(HashSet<InstrumentationRegion> predecessors)
    {
      foreach (var region in predecessors)
      {
        var blockGraph = Graph<Block>.BuildBlockGraph(region.Blocks());

        Block devBlock = null;
        CallCmd devCall = null;

        foreach (var block in region.Blocks())
        {
          foreach (var call in block.Cmds.OfType<CallCmd>())
          {
            if (devBlock == null && (predecessors.Any(val =>
              val.Implementation().Name.Equals(call.callee)) ||
              this.ChangingRegion.Implementation().Name.Equals(call.callee)))
            {
              devBlock = block;
              devCall = call;
              break;
            }
          }

          if (devBlock != null)
            break;
        }

        this.SimplifyAccessInBlocks(blockGraph, devBlock, devCall);
      }
    }

    private void SimplifyAccessInBlocks(Graph<Block> blockGraph, Block devBlock, CallCmd devCall)
    {
      var predecessorBlocks = blockGraph.NestedPredecessors(devBlock);
      var successorBlocks = blockGraph.NestedSuccessors(devBlock);
//      predecessorBlocks.RemoveWhere(val =>
//        successorBlocks.Contains(val) || val.Equals(devBlock));

      foreach (var block in predecessorBlocks)
      {
        foreach (var call in block.Cmds.OfType<CallCmd>())
        {
          if (!(call.callee.StartsWith("_WRITE_LS_$M.") ||
            call.callee.StartsWith("_READ_LS_$M.")))
            continue;

          call.callee = "_NO_OP_$" + this.EP.Name;
          call.Ins.Clear();
          call.Outs.Clear();
        }
      }

      if (!successorBlocks.Contains(devBlock))
      {
        foreach (var call in devBlock.Cmds.OfType<CallCmd>())
        {
          if (call.Equals(devCall))
            break;
          if (!(call.callee.StartsWith("_WRITE_LS_$M.") ||
            call.callee.StartsWith("_READ_LS_$M.")))
            continue;

          call.callee = "_NO_OP_$" + this.EP.Name;
          call.Ins.Clear();
          call.Outs.Clear();
        }
      }
    }

    private void SliceRegion(InstrumentationRegion region)
    {
      this.AC.TopLevelDeclarations.RemoveAll(val =>
        (val is Procedure && (val as Procedure).Name.Equals(region.Implementation().Name)) ||
        (val is Implementation && (val as Implementation).Name.Equals(region.Implementation().Name)) ||
        (val is Constant && (val as Constant).Name.Equals(region.Implementation().Name)));
      this.AC.InstrumentationRegions.Remove(region);
      this.EP.CallGraph.Remove(region);
    }

    #endregion
  }
}
