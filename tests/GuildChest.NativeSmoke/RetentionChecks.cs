using System;
using System.Collections;
using System.IO;
using GuildChest.Core;
using UnityEngine;

namespace GuildChest.NativeSmoke;

public sealed partial class NativeSmokeHarness
{
    private IEnumerator RunRetention()
    {
        yield return new WaitForSeconds(2);
        StoreSnapshot snapshot;
        try
        {
            Check(Store!.ChestCount == 0, "Retention fixture has no access points");
            if (File.Exists(Marker)) ValidateItems(Decode(Store.Snapshot().Inventory));
            var chest = UnityEngine.Object.Instantiate(ZNetScene.instance.GetPrefab("GuildChest"), new Vector3(5000, 1000, 5000), Quaternion.identity);
            Check(Store.ChestCount == 1, "Rebuilt chest registered");
            var zdo = chest.GetComponent<ZNetView>().GetZDO();
            Check(Store.Open(ZNet.GetUID(), zdo.m_uid.ToString(), Time.unscaledTime, out var lease) == AccessResult.Accepted, "Rebuilt chest grants access");
            if (!File.Exists(Marker))
            {
                var bytes = Encode(CreateFixtureInventory());
                Check(Store.Commit(ZNet.GetUID(), lease!.Token, Store.Snapshot().Revision, 1, bytes, Time.unscaledTime) == AccessResult.Accepted, "Fixture commit");
                host.GetMethod("Persist", Flags)!.Invoke(null, null);
            }
            ValidateItems(Decode(Store.Snapshot().Inventory));
            snapshot = Store.Snapshot();
            zdo.SetOwner(ZNet.GetUID()); ZDOMan.instance.DestroyZDO(zdo);
        }
        catch (Exception exception) { Fail(exception); yield break; }
        yield return new WaitForSeconds(3);
        try
        {
            ValidateRetainedInventory(snapshot);
            ZNet.instance.Save(true);
            if (!File.Exists(Marker))
            {
                File.WriteAllText(Marker, "phase1: final-chest retention, lease revocation, metadata and world save without chests passed\n");
                Logger.LogInfo("NATIVE_SMOKE_RETENTION_PHASE1_PASSED");
            }
            else
            {
                File.AppendAllText(Marker, "phase2: reload without chests, rebuilt chest access, metadata and repeated final-chest retention passed\n");
                Logger.LogInfo("NATIVE_SMOKE_RETENTION_PHASE2_PASSED");
            }
        }
        catch (Exception exception) { Fail(exception); yield break; }
        yield return new WaitForSeconds(2); Application.Quit();
    }
}
