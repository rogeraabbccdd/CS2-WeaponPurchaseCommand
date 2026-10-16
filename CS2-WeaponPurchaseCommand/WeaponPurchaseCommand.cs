using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Translations;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace WeaponPurchaseCommand
{
    public class WeaponPurchaseCommand : BasePlugin
    {
        public override string ModuleName => "CS2-Weapon Purchase Command";
        public override string ModuleVersion => "1.2";
        public override string ModuleAuthor => "Oylsister";
        public override string ModuleDescription => "Purchase weapon command for counter-strike 2";

        public ConfigFile? PurchaseConfig { get; private set; }

        public bool ConfigIsLoaded { get; set; } = false;
        public bool CommandAssigned { get; set; } = false;

        public Dictionary<int, PurchaseHistory> PlayerBuyList { get; set; } = new();

        public override void Load(bool hotReload)
        {
            AddCommand("css_weaponlist", "Command Check Weapon list", CheckWeaponListCommand);
            RegisterListener<Listeners.OnMapStart>(OnMapStart);
            RegisterListener<Listeners.OnClientPutInServer>(OnClientPutInServer);
            RegisterListener<Listeners.OnClientDisconnect>(OnClientDisconnect);
        }

        public void OnMapStart(string mapname)
        {
            var configPath = Path.Combine(ModuleDirectory, "weapons.json");

            if (!File.Exists(configPath))
            {
                Logger.LogError("There is no config");
                ConfigIsLoaded = false;
                return;
            }

            PurchaseConfig = JsonConvert.DeserializeObject<ConfigFile>(File.ReadAllText(configPath));
            Logger.LogInformation("Weapon Config is loaded");
            ConfigIsLoaded = true;

            InitialCommand();

            MemoryFunctionWithReturn<CCSPlayer_WeaponServices, CBasePlayerWeapon, bool> CCSPlayer_WeaponServices_CanUseFunc = new(GameData.GetSignature("CCSPlayer_WeaponServices_CanUse"));
            CCSPlayer_WeaponServices_CanUseFunc.Hook(OnWeaponCanUse, HookMode.Pre);
        }

        public void OnClientPutInServer(int playerSlot)
        {
            var client = Utilities.GetPlayerFromSlot(playerSlot);

            if (client != null)
            {
                PlayerBuyList.Add(client.Slot, new PurchaseHistory());
            }
        }

        public void OnClientDisconnect(int playerSlot)
        {
            var client = Utilities.GetPlayerFromSlot(playerSlot);

            if (client != null)
            {
                PlayerBuyList.Remove(client.Slot);
            }
        }

        [GameEventHandler]
        public HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
        {
            var client = @event.Userid;

            if (client != null && PlayerBuyList.ContainsKey(client.Slot))
                PlayerBuyList[client.Slot].PlayerBuyHistory.Clear();

            return HookResult.Continue;
        }

        public void CheckWeaponListCommand(CCSPlayerController? client, CommandInfo info)
        {
            if (client == null)
            {
                return;
            }

            foreach (string KeyValue in PurchaseConfig!.WeaponConfigs.Keys)
            {
                client.PrintToChat(Localizer.ForPlayer(client, "WeaponList.Key", KeyValue));

                string commandlist = "";

                foreach (string commandValue in PurchaseConfig.WeaponConfigs[KeyValue].PurchaseCommand)
                {
                    commandlist += commandValue + ",";
                }
                client.PrintToChat(Localizer.ForPlayer(client, "WeaponList.Command", commandlist));
            }
        }

        private void InitialCommand()
        {
            if (CommandAssigned)
            {
                return;
            }

            if (PurchaseConfig == null)
                return;

            foreach (var weapon in PurchaseConfig.WeaponConfigs)
            {
                foreach (var command in weapon.Value.PurchaseCommand)
                {
                    AddCommand(command, "Buy Command", PurchaseWeaponCommand);
                }
            }

            CommandAssigned = true;
        }

        public void PurchaseWeaponCommand(CCSPlayerController? client, CommandInfo info)
        {
            var weaponCommand = info.GetArg(0);

            foreach (string keyVar in PurchaseConfig!.WeaponConfigs.Keys)
            {
                foreach (string command in PurchaseConfig.WeaponConfigs[keyVar].PurchaseCommand)
                {
                    if (weaponCommand == command)
                    {
                        // info.ReplyToCommand($"{weaponCommand} with {command}");
                        PurchaseWeapon(client!, keyVar);
                        break;
                    }
                }
            }
        }

        public void PurchaseWeapon(CCSPlayerController client, string weapon)
        {
            if (client == null)
            {
                return;
            }

            var weaponConfig = PurchaseConfig!.WeaponConfigs[weapon];

            if (weaponConfig == null)
            {
                PrintMessage(client, "Purchase.InvalidWeapon");
                return;
            }

            if (!client.PawnIsAlive)
            {
                PrintMessage(client, "Purchase.NotAlive");
                return;
            }

            if (client.PlayerPawn.Value == null || !client.PlayerPawn.Value.InBuyZone)
            {
                PrintMessage(client, "Purchase.NotInBuyZone");
                return;
            }

            if (weaponConfig.PurchaseRestrict)
            {
                PrintMessage(client, "Purchase.Restricted", weapon);
                return;
            }

            var cooldown = PurchaseConfig!.CooldownPurchase;
            if (cooldown > 0 && PlayerBuyList[client.Slot].IsCooldownNow)
            {
                PrintMessage(client, "Purchase.Cooldown");
                return;
            }

            var clientMoney = client.InGameMoneyServices!.Account;

            if (clientMoney < weaponConfig.PurchasePrice)
            {
                PrintMessage(client, "Purchase.NotEnoughMoney");
                return;
            }

            int weaponPurchased;
            bool weaponFound = PlayerBuyList[client.Slot].PlayerBuyHistory.TryGetValue(weapon, out weaponPurchased);

            if (weaponConfig.PurchaseLimit > 0)
            {
                if (weaponFound)
                {
                    if (weaponPurchased >= weaponConfig.PurchaseLimit)
                    {
                        PrintMessage(client, "Purchase.LimitReached", weapon);
                        return;
                    }
                    else
                    {
                        //client.PrintToChat($"{ChatColors.Lime}{weapon}{ChatColors.Default} Purchased: {weaponPurchased}");
                        PlayerBuyList[client.Slot].PlayerBuyHistory[weapon] = weaponPurchased + 1;
                    }
                }
                else
                {
                    PlayerBuyList[client.Slot].PlayerBuyHistory.Add(weapon, 1);
                }

                PrintMessage(client, "Purchase.SuccessWithLimit", weapon, weaponConfig.PurchaseLimit - weaponPurchased - 1, weaponConfig.PurchaseLimit);
            }

            else
            {
                PrintMessage(client, "Purchase.Success", weapon);
            }

            if (HasWeaponInSlot(client, (gear_slot_t)weaponConfig.WeaponSlot))
            {
                client.DropActiveWeapon();
                AddTimer(0.1f, () =>
                {
                    client.ExecuteClientCommand($"slot{weaponConfig.WeaponSlot}");
                });
            }

            AddTimer(0.2f, () =>
            {
                client.GiveNamedItem(weaponConfig.WeaponEntity!);
            });

            AddTimer(0.3f, () =>
            {
                client.ExecuteClientCommand($"slot{weaponConfig.WeaponSlot}");
            });

            client.InGameMoneyServices.Account = clientMoney - weaponConfig.PurchasePrice;
            Utilities.SetStateChanged(client, "CCSPlayerController", "m_pInGameMoneyServices");

            PlayerBuyList[client.Slot].IsCooldownNow = true;


            AddTimer(cooldown, () =>
            {
                PlayerBuyList[client.Slot].IsCooldownNow = false;
            });
        }
        private HookResult OnWeaponCanUse(DynamicHook hook)
        {
            var weapon = hook.GetParam<CBasePlayerWeapon>(1);

            var weaponKey = GetWeaponKeyByEntityName(weapon.DesignerName);

            if (IsWeaponRestricted(weaponKey))
            {
                hook.SetReturn(false);
                return HookResult.Handled;
            }

            return HookResult.Continue;
        }

        public bool IsWeaponRestricted(string weaponKey)
        {
            if (!PurchaseConfig!.WeaponConfigs.ContainsKey(weaponKey))
                return false;

            return PurchaseConfig!.WeaponConfigs[weaponKey].PurchaseRestrict;
        }

        public string GetWeaponKeyByEntityName(string entityName)
        {
            foreach (string keyVar in PurchaseConfig!.WeaponConfigs.Keys)
            {
                if (entityName == PurchaseConfig!.WeaponConfigs[keyVar].WeaponEntity)
                    return keyVar;
            }
            return "none";
        }

        private void PrintMessage(CCSPlayerController client, string key, params object[] args)
        {
            client.PrintToChat($" {Localizer.ForPlayer(client, "Prefix")} {Localizer.ForPlayer(client, key, args)}");
        }

        public bool HasWeaponInSlot(CCSPlayerController player, gear_slot_t slot)
        {
            var weaponServices = player.PlayerPawn.Value?.WeaponServices;
            if (weaponServices == null)
                return false;

            return weaponServices.MyWeapons.Any(weapon =>
            {
                var ccsWeaponBase = weapon.Value?.As<CCSWeaponBase>();
                return ccsWeaponBase?.IsValid == true && ccsWeaponBase.VData?.GearSlot == slot;
            });
        }
    }
}

public class ConfigFile
{
    public float CooldownPurchase { get; set; } = 0f;

    public Dictionary<string, WeaponConfig> WeaponConfigs { get; set; } = new Dictionary<string, WeaponConfig>();

    public ConfigFile()
    {
        List<string> commandlist = new List<string> { "css_glock" };
        WeaponConfigs = new Dictionary<string, WeaponConfig>(StringComparer.OrdinalIgnoreCase)
        {
            { "Glock", new WeaponConfig(commandlist, "weapon_glock", 1, 100, 0, true) },
        };
    }
}

public class PurchaseHistory
{
    public Dictionary<string, int> PlayerBuyHistory { get; set; } = new Dictionary<string, int>();
    public bool IsCooldownNow { get; set; } = false;
}

public class WeaponConfig
{
    public WeaponConfig(List<string> purchaseCommand, string weaponEntity, int slot, int price, int limitbuy, bool restrict)
    {
        PurchaseCommand = purchaseCommand;
        WeaponEntity = weaponEntity;
        WeaponSlot = slot;
        PurchasePrice = price;
        PurchaseLimit = limitbuy;
        PurchaseRestrict = restrict;
    }

    public List<string> PurchaseCommand { get; set; } = new List<string>();
    public string? WeaponEntity { get; set; }
    public int WeaponSlot { get; set; }
    public int PurchasePrice { get; set; }
    public int PurchaseLimit { get; set; } = 0;
    public bool PurchaseRestrict { get; set; }
}