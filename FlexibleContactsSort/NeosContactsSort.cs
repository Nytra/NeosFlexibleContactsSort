using BaseX;
using CloudX.Shared;
using FrooxEngine;
using FrooxEngine.UIX;
using HarmonyLib;
using NeosModLoader;
using System;
using System.Collections.Generic;
using System.Linq;

namespace FlexibleContactsSort
{
    public class FlexibleContactsSort : NeosMod
    {
        public static ModConfiguration Config;

        [AutoRegisterConfigKey]
        private static ModConfigurationKey<int> AlphabeticPriority = new ModConfigurationKey<int>("AlphabeticPriority", "Priority of the contact's name. Set 0 to ignore; negative to invert.", () => 1);

        [AutoRegisterConfigKey]
        private static ModConfigurationKey<int> OnlineStatusPriority = new ModConfigurationKey<int>("OnlineStatusPriority", "Priority of the contact's online status. Set 0 to ignore; negative to invert.", () => 10);

        [AutoRegisterConfigKey]
        private static ModConfigurationKey<int> JoinablePriority = new ModConfigurationKey<int>("JoinablePriority", "Priority of the contact being in a session you can join. Set 0 to ignore; negative to invert.", () => 0);

        [AutoRegisterConfigKey]
        private static ModConfigurationKey<int> ContactRequestPriority = new ModConfigurationKey<int>("ContactRequestPriority", "Priority of the contact being a new request. Set 0 to ignore; negative to invert.", () => 1000);

        [AutoRegisterConfigKey]
        private static ModConfigurationKey<string[]> PinnedContactsKey = new ModConfigurationKey<string[]>("PinnedContacts", "List of Contacts to always keep at the top.", () => new string[0], internalAccessOnly: true);

        private static HashSet<string> PinnedContacts = new HashSet<string>();

        public override string Name => "FlexibleContactsSort";
        public override string Author => "Banane9";
        public override string Version => "2.1.0";
        public override string Link => "https://github.com/Banane9/NeosFlexibleContactsSort";

        public override void OnEngineInit()
        {
#if DEBUG
            Warn($"Extremely verbose debug logging is enabled in this build. This probably means Banane9 messed up and gave you a debug build.");
#endif
            Harmony harmony = new Harmony($"{Author}.{Name}");
            Config = GetConfiguration()!;
            Config.Save(true);

            foreach (var contact in Config.GetValue(PinnedContactsKey)!)
                PinnedContacts.Add(contact);

            harmony.PatchAll();
        }

        [HarmonyPatch(typeof(FriendsDialog))]
        private static class FriendsDialogPatch
        {
            [HarmonyPostfix]
            [HarmonyPatch("UpdateSelectedFriend")]
            private static void UpdateSelectedFriendPostfix(FriendsDialog __instance, UIBuilder ___actionsUi)
            {
                if (__instance.SelectedFriend == null || __instance.SelectedFriend.FriendUserId == "U-Neos")
                    return;

                var pinButton = ___actionsUi.Button(PinnedContacts.Contains(__instance.SelectedFriendId) ? "Unpin Contact" : "Pin Contact");
                pinButton.LocalPressed += (button, data) =>
                {
                    if (PinnedContacts.Contains(__instance.SelectedFriendId))
                    {
                        PinnedContacts.Remove(__instance.SelectedFriendId);
                        pinButton.LabelText = "Pin Contact";
                    }
                    else
                    {
                        PinnedContacts.Add(__instance.SelectedFriendId);
                        pinButton.LabelText = "Unpin Contact";
                    }

                    Config.Set(PinnedContactsKey, PinnedContacts.ToArray());
                    Config.Save(true);
                };
            }

            [HarmonyPrefix]
            [HarmonyPatch("OnCommonUpdate")]
            private static void Prefix(ref bool ___sortList, out bool __state)
            {
                // steal the sortList bool's value, and force it to false from Neos's perspective
                __state = ___sortList;
                ___sortList = false;
            }

            [HarmonyPostfix]
            [HarmonyPatch("OnCommonUpdate")]
            private static void Postfix(bool __state, SyncRef<Slot> ____listRoot)
            {
                // if Neos would have sorted (but we prevented it)
                if (__state)
                {
                    // we need to sort
                    ____listRoot.Target.SortChildren((slot1, slot2) =>
                    {
                        var friendItem1 = slot1.GetComponent<FriendItem>();
                        var friendItem2 = slot2.GetComponent<FriendItem>();
                        var friend1 = friendItem1?.Friend;
                        var friend2 = friendItem2?.Friend;

                        // nulls go last, no need to build score
                        if (friend1 != null && friend2 == null) return -1;
                        if (friend1 == null && friend2 != null) return 1;
                        if (friend1 == null && friend2 == null) return 0;

                        var score1 = CalculateFriendOrderScore(friendItem1!);
                        var score2 = CalculateFriendOrderScore(friendItem2!);

                        // sort by name
                        var alphabetical = string.Compare(friend1!.FriendUsername, friend2!.FriendUsername, StringComparison.CurrentCultureIgnoreCase);
                        score1 += alphabetical * Config.GetValue(AlphabeticPriority);
                        score2 -= alphabetical * Config.GetValue(AlphabeticPriority);

                        return score1 - score2;
                    });

#if DEBUG
                    Debug("BIG FRIEND DEBUG:");
                    foreach (Slot slot in ____listRoot.Target.Children)
                    {
                        FriendItem? component = slot.GetComponent<FriendItem>();
                        Friend? friend = component?.Friend;
                        if (friend != null)
                        {
                            Debug($"  {GetOnlineStatusOrderNumber(friend)}: \"{friend.FriendUsername}\" status={friend.FriendStatus} online={friend.UserStatus?.OnlineStatus} incoming={friend.IsAccepted}");
                        }
                    }
#endif
                }
            }
        }

        private static int CalculateFriendOrderScore(FriendItem friendItem)
        {
            var friend = friendItem.Friend;
            var pinned = PinnedContacts.Contains(friend.FriendUserId);

            var score = 0;
            var unreadOrPinned = HasUnreadMessages(friendItem) || pinned;
            
            score += friend.FriendUserId == "U-Neos" ? -101_000_000 : 0;
            score += unreadOrPinned ? -100_000_000 : 0;

            score += friend.FriendStatus == FriendStatus.SearchResult ? 100_000_000 : 0; // non-contact search results always at the end

            // Ignore offline status if this contact has unread messages or is pinned
            score += (IsOfflineContact(friend) && !unreadOrPinned) ? 10_000_000 : 0; // offline friends before results

            score += IsOutgoingRequest(friend) ? 9_000_000 : 0; // outgoing requests before offline

            score += (IsIncomingRequest(friend) ? 0 : 1) * Config.GetValue(ContactRequestPriority);
            score += (IsInJoinableSession(friend) ? 0 : 1) * Config.GetValue(JoinablePriority);
            score += GetOnlineStatusOrderNumber(friend) * Config.GetValue(OnlineStatusPriority);

            return score;
        }

        private static bool HasUnreadMessages(FriendItem friend)
        {
            return friend.HasMessages;
        }

        private static bool IsOutgoingRequest(Friend friend)
        {
            return friend.FriendStatus == FriendStatus.Accepted && !friend.IsAccepted;
        }

        private static bool IsIncomingRequest(Friend friend)
        {
            return friend.FriendStatus == FriendStatus.Requested;
        }

        private static bool IsOfflineContact(Friend friend)
        {
            var status = friend.UserStatus?.OnlineStatus ?? OnlineStatus.Offline;

            return friend.FriendStatus == FriendStatus.Accepted && friend.IsAccepted
                && status == OnlineStatus.Offline || status == OnlineStatus.Invisible;
        }

        [HarmonyPatch(typeof(NeosUIStyle), nameof(NeosUIStyle.GetStatusColor))]
        private static class NeosUIStylePatch
        {
            private static void Postfix(Friend friend, ref color __result)
            {
                var onlineStatus = friend.UserStatus?.OnlineStatus ?? OnlineStatus.Offline;

                if (onlineStatus == OnlineStatus.Offline && friend.FriendStatus == FriendStatus.Accepted && !friend.IsAccepted)
                    __result = color.Yellow;
            }
        }

        private static bool IsInJoinableSession(Friend friend)
        {
            return friend.UserStatus is UserStatus status
                && status.CurrentSession != null && status?.CompatibilityHash == Engine.Current.CompatibilityHash;
        }

        // lower numbers appear earlier in the list
        private static int GetOnlineStatusOrderNumber(Friend friend)
        {
            var status = friend.UserStatus?.OnlineStatus ?? OnlineStatus.Offline;
            switch (status)
            {
                case OnlineStatus.Online:
                    return 0;

                case OnlineStatus.Away:
                    return 1;

                case OnlineStatus.Busy:
                    return 2;

                default:
                    return 3;
                    // unsure how people with no relation, ignored, or blocked will appear... but they'll end up here too
            }
        }
    }
}