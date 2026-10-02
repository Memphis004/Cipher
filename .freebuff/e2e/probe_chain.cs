using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using ProjectSpy.Infrastructure;
using ProjectSpy.Infrastructure.Blockchain;
using Bencodex.Types;
using Libplanet.Crypto;
using UnityEngine;

public static class Script
{
    public static void Main()
    {
        if (!Application.isPlaying)
        {
            Debug.Log("[cprobe] NOT PLAYING");
            return;
        }
        RunAsync().Forget();
    }

    private static async UniTaskVoid RunAsync()
    {
        try
        {
            RootLifetimeScope root = UnityEngine.Object.FindObjectOfType<RootLifetimeScope>(true);
            var chain = (ILibplanetClient)root.Container.Resolve(typeof(ILibplanetClient));
            var watcher = (StateWatcher)root.Container.Resolve(typeof(StateWatcher));
            var key = (KeyStore)root.Container.Resolve(typeof(KeyStore));

            await UniTask.Delay(TimeSpan.FromSeconds(1.0f));
            Address addr = key.LoadOrCreatePlayerKey().Address;

            Debug.Log("[cprobe] status=" + chain.Status
                + " tip=" + chain.TipIndex
                + " peers=" + chain.PeerCount
                + " addr=" + addr);

            IValue? avatar = chain.GetState(ProjectSpy.Lib.Addresses.Avatar, addr);
            string avatarDesc;
            if (avatar is Dictionary d)
            {
                string nm = d.GetValue<Text>((Text)"name").Value;
                long gold = (long)d.GetValue<Integer>((Text)"gold").Value;
                avatarDesc = "dict(name=" + nm + ", gold=" + gold + ")";
            }
            else
            {
                avatarDesc = avatar is null ? "NULL" : avatar.GetType().Name;
            }
            Debug.Log("[cprobe] avatarState=" + avatarDesc);

            IValue? inv = chain.GetState(ProjectSpy.Lib.Addresses.Inventory, addr);
            Debug.Log("[cprobe] inventory=" + (inv is Dictionary ? "dict" : inv is null ? "NULL" : inv.GetType().Name));

            AvatarSnapshot? snap = watcher.Current;
            Debug.Log("[cprobe] snapshot: name=" + (snap?.Name ?? "-")
                + " gold=" + (snap?.Gold ?? -1)
                + " fish3001=" + (snap?.GetItemCount(3001) ?? -1)
                + " salt6001=" + (snap?.GetItemCount(6001) ?? -1));
        }
        catch (Exception ex)
        {
            Debug.LogError("[cprobe] FAILED - " + ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace);
        }
    }
}
