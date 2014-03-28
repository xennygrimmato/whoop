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

namespace whoop
{
  public class SharedStateAbstractor
  {
    WhoopProgram wp;

    public SharedStateAbstractor(WhoopProgram wp)
    {
      Contract.Requires(wp != null);
      this.wp = wp;
    }

    public void Run()
    {
      AbstractEntryPoints();
      AbstractInitFuncs();
      AbstractOtherFuncs();
    }

    private void AbstractEntryPoints()
    {
      foreach (var impl in wp.GetImplementationsToAnalyse()) {
        AbstractReadAccesses(impl);
        AbstractWriteAccesses(impl);
      }
    }

    private void AbstractInitFuncs()
    {
      foreach (var impl in wp.GetInitFunctions()) {
        AbstractReadAccesses(impl);
        AbstractWriteAccesses(impl);
      }
    }

    private void AbstractOtherFuncs()
    {
      foreach (var impl in wp.program.TopLevelDeclarations.OfType<Implementation>()) {
        if (wp.isWhoopFunc(impl)) continue;
        if (wp.GetImplementationsToAnalyse().Exists(val => val.Name.Equals(impl.Name))) continue;
        if (wp.GetInitFunctions().Exists(val => val.Name.Equals(impl.Name))) continue;
        if (!wp.isCalledByAnEntryPoint(impl)) continue;

        AbstractReadAccesses(impl);
        AbstractWriteAccesses(impl);
      }
    }

    private void AbstractReadAccesses(Implementation impl)
    {
      foreach (var b in impl.Blocks) {
        for (int k = 0; k < b.Cmds.Count; k++) {
          if (!(b.Cmds[k] is AssignCmd)) continue;

          foreach (var rhs in (b.Cmds[k] as AssignCmd).Rhss.OfType<NAryExpr>()) {
            if (!(rhs.Fun is MapSelect) || rhs.Args.Count != 2 ||
              !((rhs.Args[0] as IdentifierExpr).Name.Contains("$M.")))
              continue;

            Variable v = (b.Cmds[k] as AssignCmd).Lhss[0].DeepAssignedVariable;
            HavocCmd havoc = new HavocCmd(Token.NoToken,
              new List<IdentifierExpr> { new IdentifierExpr(v.tok, v) });
            b.Cmds[k] = havoc;
          }
        }
      }
    }

    private void AbstractWriteAccesses(Implementation impl)
    {
      foreach (var b in impl.Blocks) {
        List<Cmd> cmdsToRemove = new List<Cmd>();

        for (int k = 0; k < b.Cmds.Count; k++) {
          if (!(b.Cmds[k] is AssignCmd)) continue;

          foreach (var lhs in (b.Cmds[k] as AssignCmd).Lhss.OfType<MapAssignLhs>()) {
            if (!(lhs.DeepAssignedIdentifier.Name.Contains("$M.")) ||
              !(lhs.Map is SimpleAssignLhs) || lhs.Indexes.Count != 1)
              continue;

            cmdsToRemove.Add(b.Cmds[k]);
          }
        }

        foreach (var c in cmdsToRemove) b.Cmds.Remove(c);
      }
    }
  }
}
