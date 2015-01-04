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
using System.Security.Policy;

namespace Whoop.Regions
{
  internal class InstrumentationRegion : IRegion
  {
    #region fields

    protected AnalysisContext AC;

    protected string RegionName;

    private Implementation InternalImplementation;

    protected Block RegionHeader;
    protected List<Block> RegionBlocks;

    internal Dictionary<string, List<Expr>> ResourceAccesses;
    internal HashSet<string> ResourcesWithUnidentifiedAccesses;
    internal bool IsResourceAnalysisDone;

    #endregion

    #region constructors

    public InstrumentationRegion(AnalysisContext ac, Implementation impl)
    {
      Contract.Requires(ac != null);
      this.AC = ac;
      this.RegionName = impl.Name + "$instrumented";
      this.ProcessRegionBlocks(impl);
      this.ProcessWrapperImplementation(impl);
      this.ProcessWrapperProcedure(impl);
      this.ResourceAccesses = new Dictionary<string, List<Expr>>();
      this.ResourcesWithUnidentifiedAccesses = new HashSet<string>();
      this.IsResourceAnalysisDone = false;
    }

    #endregion

    #region public API

    public object Identifier()
    {
      return this.RegionHeader;
    }

    public string Name()
    {
      return this.RegionName;
    }

    public Block Header()
    {
      return this.RegionHeader;
    }

    public Implementation Implementation()
    {
      return this.InternalImplementation;
    }

    public Procedure Procedure()
    {
      return this.InternalImplementation.Proc;
    }

    public List<Block> Blocks()
    {
      return this.RegionBlocks;
    }

    public IEnumerable<Cmd> Cmds()
    {
      foreach (var b in this.RegionBlocks)
        foreach (Cmd c in b.Cmds)
          yield return c;
    }

    public IEnumerable<object> CmdsChildRegions()
    {
      return Enumerable.Empty<object>();
    }

    public IEnumerable<IRegion> SubRegions()
    {
      return Enumerable.Empty<IRegion>();
    }

    public IEnumerable<Block> PreHeaders()
    {
      return Enumerable.Empty<Block>();
    }

    public Expr Guard()
    {
      return null;
    }

    public void AddInvariant(PredicateCmd cmd)
    {
      this.RegionHeader.Cmds.Insert(0, cmd);
    }

    public List<PredicateCmd> RemoveInvariants()
    {
      List<PredicateCmd> result = new List<PredicateCmd>();
      List<Cmd> newCmds = new List<Cmd>();
      bool removedAllInvariants = false;

      foreach (Cmd c in this.RegionHeader.Cmds)
      {
        if (!(c is PredicateCmd))
          removedAllInvariants = true;
        if (c is PredicateCmd && !removedAllInvariants)
          result.Add((PredicateCmd)c);
        else
          newCmds.Add(c);
      }

      this.RegionHeader.Cmds = newCmds;

      return result;
    }

    #endregion

    #region resource analysis related methods

    public bool TryAddResourceAccess(string resource, Expr access)
    {
      if (access == null)
      {
        this.ResourcesWithUnidentifiedAccesses.Add(resource);
        return false;
      }

      if (!this.ResourceAccesses.ContainsKey(resource))
      {
        this.ResourceAccesses.Add(resource, new List<Expr> { access });

        if (this.ResourcesWithUnidentifiedAccesses.Contains(resource))
          this.ResourcesWithUnidentifiedAccesses.Remove(resource);

        return true;
      }
      else if (this.ResourceAccesses[resource].Any(val =>
        val.ToString().Equals(access.ToString())))
      {
        return false;
      }
      else
      {
        this.ResourceAccesses[resource].Add(access);

        if (this.ResourcesWithUnidentifiedAccesses.Contains(resource))
          this.ResourcesWithUnidentifiedAccesses.Remove(resource);

        return true;
      }
    }

    #endregion

    #region construction methods

    private void ProcessWrapperImplementation(Implementation impl)
    {
      this.InternalImplementation = impl;
    }

    private void ProcessWrapperProcedure(Implementation impl)
    {
      this.InternalImplementation.Proc = impl.Proc;
    }

    private void ProcessRegionBlocks(Implementation impl)
    {
      this.RegionBlocks = impl.Blocks;
      this.RegionHeader = this.CreateRegionHeader();
    }

    #endregion

    #region helper methods

    private Block CreateRegionHeader()
    {
      Block header = new Block(Token.NoToken, "$header",
        new List<Cmd>(), new GotoCmd(Token.NoToken,
          new List<string> { this.RegionBlocks[0].Label }));
      this.RegionBlocks.Insert(0, header);
      return header;
    }

    #endregion
  }
}
