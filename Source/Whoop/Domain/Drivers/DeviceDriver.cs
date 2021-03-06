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
using System.IO;
using System.Xml.Linq;

using Microsoft.Boogie;
using Microsoft.Basetypes;

using Whoop.IO;

namespace Whoop.Domain.Drivers
{
  public static class DeviceDriver
  {
    #region fields

    public static List<EntryPoint> EntryPoints;
    public static List<EntryPointPair> EntryPointPairs;

    public static List<Module> Modules;

    public static string InitEntryPoint
    {
      get;
      private set;
    }

    public static string SharedStructInitialiseFunc
    {
      get;
      private set;
    }

    #endregion

    #region public API

    /// <summary>
    /// Parses and initializes device driver specific information.
    /// </summary>
    /// <param name="files">List of file names</param>
    public static void ParseAndInitialize(List<string> files)
    {
      string driverInfoFile = files[files.Count - 1].Substring(0,
        files[files.Count - 1].LastIndexOf(".")) + ".info";

      DeviceDriver.EntryPoints = new List<EntryPoint>();
      DeviceDriver.Modules = new List<Module>();
      DeviceDriver.SharedStructInitialiseFunc = "";

      bool whoopInit = true;
      using(StreamReader file = new StreamReader(driverInfoFile))
      {
        string line;

        while ((line = file.ReadLine()) != null)
        {
          string type = line.Trim(new char[] { '<', '>' });
          string api = "";
          string kernelFunc = "";

          if (type.Contains("$"))
          {
            var moduleSplit = type.Split(new string[] { "$" }, StringSplitOptions.None);
            api = moduleSplit[0];
            kernelFunc = moduleSplit[1];
          }
          else
          {
            api = type;
          }

          Module module = new Module(api, kernelFunc);
          DeviceDriver.Modules.Add(module);

          if (api.Equals("test_driver") ||
            api.Equals("pci_driver") ||
            api.Equals("usb_driver") ||
            api.Equals("usb_serial_driver") ||
            api.Equals("platform_driver") ||
            api.Equals("ps3_system_bus_driver") ||
            api.Equals("cx_drv"))
          {
            whoopInit = false;
          }

          while ((line = file.ReadLine()) != null)
            if (line.Equals("</>")) break;
        }
      }

      using(StreamReader file = new StreamReader(driverInfoFile))
      {
        string line;

        while ((line = file.ReadLine()) != null)
        {
          string type = line.Trim(new char[] { '<', '>' });
          string api = "";
          string kernelFunc = "";

          if (type.Contains("$"))
          {
            var moduleSplit = type.Split(new string[] { "$" }, StringSplitOptions.None);
            api = moduleSplit[0];
            kernelFunc = moduleSplit[1];
          }
          else
          {
            api = type;
          }

          if (api.Equals("whoop_network_shared_struct"))
          {
            var info = file.ReadLine();
            DeviceDriver.SharedStructInitialiseFunc = info.Remove(0, 2);
          }

          Module module = DeviceDriver.Modules.First(val => val.API.Equals(api));

          while ((line = file.ReadLine()) != null)
          {
            if (line.Equals("</>")) break;
            string[] pair = line.Split(new string[] { "::" }, StringSplitOptions.None);

            var ep = new EntryPoint(pair[1], pair[0], kernelFunc, module, whoopInit);
            module.EntryPoints.Add(ep);

            if (DeviceDriver.EntryPoints.Any(val => val.Name.Equals(ep.Name)))
              continue;

            DeviceDriver.EntryPoints.Add(ep);

            if (ep.IsCalledWithNetworkDisabled || ep.IsGoingToDisableNetwork)
            {
              var epClone = new EntryPoint(pair[1] + "#net", pair[0], kernelFunc, module, whoopInit, true);
              module.EntryPoints.Add(epClone);
              DeviceDriver.EntryPoints.Add(epClone);
            }
          }
        }
      }

      DeviceDriver.EntryPointPairs = new List<EntryPointPair>();

      foreach (var ep1 in DeviceDriver.EntryPoints)
      {
        foreach (var ep2 in DeviceDriver.EntryPoints)
        {
          if (!DeviceDriver.CanBePaired(ep1, ep2)) continue;
          if (!DeviceDriver.IsNewPair(ep1.Name, ep2.Name)) continue;
          if (!DeviceDriver.CanRunConcurrently(ep1, ep2)) continue;
          DeviceDriver.EntryPointPairs.Add(new EntryPointPair(ep1, ep2));
        }
      }
    }

    public static EntryPoint GetEntryPoint(string name)
    {
      return DeviceDriver.EntryPoints.Find(ep => ep.Name.Equals(name));
    }

    public static HashSet<EntryPoint> GetPairs(EntryPoint ep)
    {
      var pairs = new HashSet<EntryPoint>();

      foreach (var pair in DeviceDriver.EntryPointPairs.FindAll(val =>
        val.EntryPoint1.Name.Equals(ep.Name) || val.EntryPoint2.Name.Equals(ep.Name)))
      {
        if (pair.EntryPoint1.Name.Equals(ep.Name))
          pairs.Add(pair.EntryPoint2);
        else if (pair.EntryPoint2.Name.Equals(ep.Name))
          pairs.Add(pair.EntryPoint1);
      }

      return pairs;
    }

    /// <summary>
    /// Emits the entry point pairs in an XML file.
    /// </summary>
    /// <param name="files">List of file names</param>
    public static void EmitEntryPointPairs(List<string> files)
    {
      XDocument epXmlDoc = new XDocument();
      XElement root = new XElement("driver", new XAttribute("name", WhoopCommandLineOptions.Get().OriginalFile));

      foreach (var ep in DeviceDriver.EntryPointPairs)
      {
        root.Add(new XElement("pair", new XAttribute("ep1", ep.EntryPoint1.Name),
          new XAttribute("ep2", ep.EntryPoint2.Name), new XAttribute("bug", false)));
      }

      epXmlDoc.Add(root);

      string pairXmlFile = files[files.Count - 1].Substring(0,
        files[files.Count - 1].LastIndexOf(".")) + ".pairs.xml";
      epXmlDoc.Save(pairXmlFile);
    }

    #endregion

    #region other methods

    /// <summary>
    /// Sets the initial entry point.
    /// </summary>
    /// <param name="ep">Name of the entry point</param>
    internal static void SetInitEntryPoint(string ep)
    {
      if (DeviceDriver.InitEntryPoint != null)
      {
        Console.Error.Write("Cannot have more than one init entry points.");
        Environment.Exit((int)Outcome.ParsingError);
      }

      DeviceDriver.InitEntryPoint = ep;
    }

    /// <summary>
    /// Checks if the given entry points form a new pair.
    /// </summary>
    /// <returns>Boolean value</returns>
    /// <param name="ep1">Name of first entry point</param>
    /// <param name="ep2">Name of second entry point</param>
    private static bool IsNewPair(string ep1, string ep2)
    {
      if (DeviceDriver.EntryPointPairs.Exists(val =>
        (val.EntryPoint1.Name.Equals(ep1) && (val.EntryPoint2.Name.Equals(ep2))) ||
        (val.EntryPoint1.Name.Equals(ep2) && (val.EntryPoint2.Name.Equals(ep1)))))
      {
        return false;
      }

      return true;
    }

    /// <summary>
    /// Checks if the given entry points can be paired.
    /// </summary>
    /// <returns>Boolean value</returns>
    /// <param name="ep1">first entry point</param>
    /// <param name="ep2">second entry point</param>
    private static bool CanBePaired(EntryPoint ep1, EntryPoint ep2)
    {
      if (ep1.IsCalledWithNetworkDisabled && DeviceDriver.IsNetworkAPI(ep2.API))
      {
        if (ep1.IsClone) return true;
        else return false;
      }

      if (ep2.IsCalledWithNetworkDisabled && DeviceDriver.IsNetworkAPI(ep1.API))
      {
        if (ep2.IsClone) return true;
        else return false;
      }

      if (ep1.IsGoingToDisableNetwork && DeviceDriver.IsNetworkAPI(ep2.API))
      {
        if (ep1.IsClone) return true;
        else return false;
      }

      if (ep2.IsGoingToDisableNetwork && DeviceDriver.IsNetworkAPI(ep1.API))
      {
        if (ep2.IsClone) return true;
        else return false;
      }

      if (ep1.IsClone || ep2.IsClone)
        return false;

      return true;
    }

    /// <summary>
    /// Checks if the given entry points can run concurrently.
    /// </summary>
    /// <returns>Boolean value</returns>
    /// <param name="ep1">First entry point</param>
    /// <param name="ep2">Second entry point</param>
    private static bool CanRunConcurrently(EntryPoint ep1, EntryPoint ep2)
    {
      if (ep1.IsInit && ep2.IsInit)
        return false;
      if (ep1.IsExit || ep2.IsExit)
        return false;

      if (DeviceDriver.HasKernelImposedDeviceLock(ep1.API, ep1.Module) &&
          DeviceDriver.HasKernelImposedDeviceLock(ep2.API, ep2.Module))
        return false;
      if (DeviceDriver.HasKernelImposedPowerLock(ep1.API) &&
          DeviceDriver.HasKernelImposedPowerLock(ep2.API))
        return false;
      if (DeviceDriver.HasKernelImposedRTNL(ep1.API) &&
          DeviceDriver.HasKernelImposedRTNL(ep2.API))
        return false;
      if (DeviceDriver.HasKernelImposedTxLock(ep1.API) &&
          DeviceDriver.HasKernelImposedTxLock(ep2.API))
        return false;

      if (DeviceDriver.IsPowerManagementAPI(ep1.API) &&
          DeviceDriver.IsPowerManagementAPI(ep2.API))
        return false;
      if (DeviceDriver.IsCalledWithNetpollDisabled(ep1.API) &&
          DeviceDriver.IsCalledWithNetpollDisabled(ep2.API))
        return false;

      if (DeviceDriver.IsFileOperationsSerialised(ep1, ep2))
        return false;
      if (DeviceDriver.IsBlockOperationsSerialised(ep1, ep2))
        return false;
      if (DeviceDriver.IsUSBOperationsSerialised(ep1, ep2))
        return false;
      if (DeviceDriver.IsNFCOperationsSerialised(ep1, ep2))
        return false;

      return true;
    }

    /// <summary>
    /// Checks if the entry point has been serialised by the device_lock(dev) lock.
    /// </summary>
    internal static bool HasKernelImposedDeviceLock(string name, Module module)
    {
      if (name.Equals("probe") || name.Equals("remove") ||
          name.Equals("shutdown"))
        return true;

      // power management API
      if (name.Equals("prepare") || name.Equals("complete") ||
          name.Equals("resume") || name.Equals("suspend") ||
          name.Equals("freeze") || name.Equals("poweroff") ||
          name.Equals("restore") || name.Equals("thaw") ||
          name.Equals("runtime_resume") || name.Equals("runtime_suspend") ||
          name.Equals("runtime_idle"))
        return true;

      return false;
    }

    /// <summary>
    /// Checks if the entry point has been serialised by dev->power.lock.
    /// </summary>
    /// <returns>Boolean value</returns>
    /// <param name="ep">Name of entry point</param>
    internal static bool HasKernelImposedPowerLock(string ep)
    {
      // power management API
      if (ep.Equals("runtime_resume") || ep.Equals("runtime_suspend") ||
          ep.Equals("runtime_idle"))
        return true;

      return false;
    }

    /// <summary>
    /// Checks if the entry point has been serialised by the RTNL lock.
    /// </summary>
    /// <returns>Boolean value</returns>
    /// <param name="ep">Name of entry point</param>
    internal static bool HasKernelImposedRTNL(string ep)
    {
      // network device management API
      if (ep.Equals("ndo_init") || ep.Equals("ndo_uninit") ||
          ep.Equals("ndo_open") || ep.Equals("ndo_stop") ||
          ep.Equals("ndo_start_xmit") ||
          ep.Equals("ndo_validate_addr") ||
          ep.Equals("ndo_change_mtu") ||
          ep.Equals("ndo_get_stats64") || ep.Equals("ndo_get_stats") ||
          ep.Equals("ndo_poll_controller") || ep.Equals("ndo_netpoll_setup") ||
          ep.Equals("ndo_netpoll_cleanup") ||
          ep.Equals("ndo_fix_features") || ep.Equals("ndo_set_features") ||
          ep.Equals("ndo_set_mac_address") ||
          ep.Equals("ndo_do_ioctl") ||
          ep.Equals("ndo_set_rx_mode"))
        return true;

      // ethernet device management API
      if (ep.Equals("get_settings") || ep.Equals("set_settings") ||
          ep.Equals("get_drvinfo") ||
          ep.Equals("get_regs_len") || ep.Equals("get_regs") ||
          ep.Equals("get_wol") || ep.Equals("set_wol") ||
          ep.Equals("get_msglevel") || ep.Equals("set_msglevel") ||
          ep.Equals("nway_reset") || ep.Equals("get_link") ||
          ep.Equals("get_eeprom_len") ||
          ep.Equals("get_eeprom") || ep.Equals("set_eeprom") ||
          ep.Equals("get_coalesce") || ep.Equals("set_coalesce") ||
          ep.Equals("get_ringparam") || ep.Equals("set_ringparam") ||
          ep.Equals("get_pauseparam") || ep.Equals("set_pauseparam") ||
          ep.Equals("self_test") || ep.Equals("get_strings") ||
          ep.Equals("set_phys_id") || ep.Equals("get_ethtool_stats") ||
          ep.Equals("begin") || ep.Equals("complete") ||
          ep.Equals("get_priv_flags") || ep.Equals("set_priv_flags") ||
          ep.Equals("get_sset_count") ||
          ep.Equals("get_rxnfc") || ep.Equals("set_rxnfc") ||
          ep.Equals("flash_device") || ep.Equals("reset") ||
          ep.Equals("get_rxfh_indir_size") ||
          ep.Equals("get_rxfh_indir") || ep.Equals("set_rxfh_indir") ||
          ep.Equals("get_channels") || ep.Equals("set_channels") ||
          ep.Equals("get_dump_flag") || ep.Equals("get_dump_data") ||
          ep.Equals("set_dump") || ep.Equals("get_ts_info") ||
          ep.Equals("get_module_info") || ep.Equals("get_module_eeprom") ||
          ep.Equals("get_eee") || ep.Equals("set_eee"))
        return true;

      return false;
    }

    /// <summary>
    /// Checks if the entry point has been serialised by HARD_TX_LOCK.
    /// </summary>
    /// <returns>Boolean value</returns>
    /// <param name="ep">Name of entry point</param>
    internal static bool HasKernelImposedTxLock(string ep)
    {
      // network device management API
      if (ep.Equals("ndo_start_xmit"))
        return true;

      return false;
    }

    /// <summary>
    /// Checks if the file operation entry points have been serialised by
    /// the kernel.
    /// </summary>
    /// <returns>Boolean value</returns>
    /// <param name="ep">Name of entry point</param>
    internal static bool IsFileOperationsSerialised(EntryPoint ep1, EntryPoint ep2)
    {
      // file_operations API
      if (!ep1.Module.API.Equals("file_operations") ||
          !ep2.Module.API.Equals("file_operations"))
        return false;

      if (ep1.API.Equals("release") || ep2.API.Equals("release"))
        return true;

      if (ep1.API.Equals("mmap") && ep2.API.Equals("mmap"))
        return true;

      return false;
    }

    /// <summary>
    /// Checks if the usb operation entry points have been serialised by
    /// the kernel.
    /// </summary>
    /// <returns>Boolean value</returns>
    /// <param name="ep">Name of entry point</param>
    internal static bool IsUSBOperationsSerialised(EntryPoint ep1, EntryPoint ep2)
    {
      // usb_serial_driver API
      if (!ep1.Module.API.Equals("usb_serial_driver") ||
        !ep2.Module.API.Equals("usb_serial_driver"))
        return false;

      if (ep1.API.Equals("port_probe") || ep2.API.Equals("port_probe"))
        return true;
      if (ep1.API.Equals("attach") || ep2.API.Equals("attach"))
        return true;
      if (ep1.API.Equals("port_remove") || ep2.API.Equals("port_remove"))
        return true;
      if (ep1.API.Equals("open") || ep2.API.Equals("open"))
        return true;
      if (ep1.API.Equals("process_read_urb") || ep2.API.Equals("process_read_urb"))
        return true;

      if ((ep1.API.Equals("tiocmget") || ep1.API.Equals("tiocmset") ||
          ep1.API.Equals("tiocmiwait") || ep1.API.Equals("get_icount") ||
          ep1.API.Equals("ioctl")) &&
          (ep2.API.Equals("tiocmget") || ep2.API.Equals("tiocmset") ||
          ep2.API.Equals("tiocmiwait") || ep2.API.Equals("get_icount") ||
          ep2.API.Equals("ioctl")))
        return true;

      if (ep1.API.Equals("set_termios") && ep2.API.Equals("set_termios"))
        return true;
      if (ep1.API.Equals("dtr_rts") && ep2.API.Equals("dtr_rts"))
        return true;

      return false;
    }

    /// <summary>
    /// Checks if the nfc operation entry points have been serialised by
    /// the kernel.
    /// </summary>
    /// <returns>Boolean value</returns>
    /// <param name="ep">Name of entry point</param>
    internal static bool IsNFCOperationsSerialised(EntryPoint ep1, EntryPoint ep2)
    {
      // nfc_ops API
      if (!ep1.Module.API.Equals("nfc_ops") ||
        !ep2.Module.API.Equals("nfc_ops"))
        return false;

      // NFC API
      if ((ep1.API.Equals("dev_up") || ep1.API.Equals("dev_down") ||
          ep1.API.Equals("dep_link_up") || ep1.API.Equals("dep_link_down") ||
          ep1.API.Equals("activate_target") || ep1.API.Equals("deactivate_target") ||
          ep1.API.Equals("im_transceive") || ep1.API.Equals("tm_send") ||
          ep1.API.Equals("start_poll") || ep1.API.Equals("stop_poll")) &&
          (ep2.API.Equals("dev_up") || ep2.API.Equals("dev_down") ||
          ep2.API.Equals("dep_link_up") || ep2.API.Equals("dep_link_down") ||
          ep2.API.Equals("activate_target") || ep2.API.Equals("deactivate_target") ||
          ep2.API.Equals("im_transceive") || ep2.API.Equals("tm_send") ||
          ep2.API.Equals("start_poll") || ep2.API.Equals("stop_poll")))
        return true;

      return false;
    }

    /// <summary>
    /// Checks if the block operation entry points have been serialised by
    /// the kernel.
    /// </summary>
    /// <returns>Boolean value</returns>
    /// <param name="ep">Name of entry point</param>
    internal static bool IsBlockOperationsSerialised(EntryPoint ep1, EntryPoint ep2)
    {
      // file_operations API
      if (!ep1.Module.API.Equals("block_device_operations") ||
        !ep2.Module.API.Equals("block_device_operations"))
        return false;

      if (ep1.API.Equals("release") || ep2.API.Equals("release"))
        return true;
      if (ep1.API.Equals("revalidate_disk") || ep2.API.Equals("revalidate_disk"))
        return true;
      if (ep1.API.Equals("open") && ep2.API.Equals("release"))
        return true;
      if (ep1.API.Equals("release") && ep2.API.Equals("open"))
        return true;

      return false;
    }

    /// <summary>
    /// Checks if it is a network entry point.
    /// </summary>
    /// <returns>Boolean value</returns>
    /// <param name="ep">Name of entry point</param>
    internal static bool IsNetworkAPI(string ep)
    {
      if (DeviceDriver.HasKernelImposedRTNL(ep))
        return true;
      if (ep.Equals("ndo_tx_timeout"))
        return true;

      return false;
    }

    /// <summary>
    /// Checks if it is a power management entry point.
    /// </summary>
    /// <returns>Boolean value</returns>
    /// <param name="ep">Name of entry point</param>
    internal static bool IsPowerManagementAPI(string ep)
    {
      // power management API
      if (ep.Equals("prepare") || ep.Equals("complete") ||
        ep.Equals("resume") || ep.Equals("suspend") ||
        ep.Equals("freeze") || ep.Equals("poweroff") ||
        ep.Equals("restore") || ep.Equals("thaw") ||
        ep.Equals("runtime_resume") || ep.Equals("runtime_suspend") ||
        ep.Equals("runtime_idle"))
        return true;

      return false;
    }

    /// <summary>
    /// Checks if the entry point will disable network.
    /// </summary>
    /// <returns>Boolean value</returns>
    /// <param name="ep">Name of entry point</param>
    internal static bool IsGoingToDisableNetwork(string ep)
    {
      if (!DeviceDriver.Modules.Any(val => val.API.Equals("net_device_ops")))
        return false;

      if (ep.Equals("suspend") || ep.Equals("freeze") ||
          ep.Equals("poweroff") || ep.Equals("runtime_suspend") ||
          ep.Equals("shutdown"))
        return true;
      return false;
    }

    /// <summary>
    /// Checks if the entry point has been called with network disabled.
    /// </summary>
    /// <returns>Boolean value</returns>
    /// <param name="ep">Name of entry point</param>
    internal static bool IsCalledWithNetworkDisabled(string ep)
    {
      if (ep.Equals("resume") || ep.Equals("restore") ||
        ep.Equals("thaw") || ep.Equals("runtime_resume"))
        return true;
      return false;
    }

    /// <summary>
    /// Checks if the entry point has been called with netpoll disabled.
    /// Netpoll is included in this set of entry points for convenience.
    /// </summary>
    /// <returns>Boolean value</returns>
    /// <param name="ep">Name of entry point</param>
    private static bool IsCalledWithNetpollDisabled(string ep)
    {
      // network device management API
      if (ep.Equals("ndo_poll_controller") ||
          ep.Equals("ndo_open") || ep.Equals("ndo_stop") ||
          ep.Equals("ndo_validate_addr"))
        return true;

      return false;
    }

    #endregion
  }
}
