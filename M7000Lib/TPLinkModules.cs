using System.Threading.Tasks;

public class TPLinkModules
{
    private readonly TPLinkProtocol _protocol;

    public TPLinkModules(TPLinkProtocol protocol)
    {
        _protocol = protocol;
    }

    public static class Names
    {
        public const string Authenticator = "authenticator";
        public const string WebServer = "webServer";
        public const string Status = "status";
        public const string WAN = "wan";
        public const string SimLock = "simLock";
        public const string Message = "message";
        public const string WLAN = "wlan";
        public const string WPS = "wps";
        public const string PowerSave = "power_save";
        public const string FlowStat = "flowstat";
        public const string ConnectedDevices = "connectedDevices";
        public const string MACFilters = "macFilters";
        public const string LAN = "lan";
        public const string Update = "update";
        public const string StorageShare = "storageShare";
        public const string Reboot = "reboot";
        public const string RestoreConf = "restoreDefaults";
        public const string Time = "time";
        public const string Log = "log";
        public const string APBridge = "apBridge";
        public const string Voice = "voice";
        public const string UPnP = "upnp";
        public const string DMZ = "dmz";
        public const string ALG = "alg";
        public const string VirtualServer = "virtualServer";
        public const string PortTriggering = "portTrigger";
    }

    public Task<string> RequestAuthenticator() => _protocol.RequestModule(Names.Authenticator);
    public Task<string> RequestWebServer() => _protocol.RequestModule(Names.WebServer);
    public Task<string> RequestStatus() => _protocol.RequestModule(Names.Status);
    public Task<string> RequestWAN() => _protocol.RequestModule(Names.WAN);
    public Task<string> RequestSimLock() => _protocol.RequestModule(Names.SimLock);
    public Task<string> RequestMessage() => _protocol.RequestModule(Names.Message);
    public Task<string> RequestWLAN() => _protocol.RequestModule(Names.WLAN);
    public Task<string> RequestWPS() => _protocol.RequestModule(Names.WPS);
    public Task<string> RequestPowerSave() => _protocol.RequestModule(Names.PowerSave);
    public Task<string> RequestFlowStat() => _protocol.RequestModule(Names.FlowStat);
    public Task<string> RequestConnectedDevices() => _protocol.RequestModule(Names.ConnectedDevices);
    public Task<string> RequestMACFilters() => _protocol.RequestModule(Names.MACFilters);
    public Task<string> RequestLAN() => _protocol.RequestModule(Names.LAN);
    public Task<string> RequestUpdate() => _protocol.RequestModule(Names.Update);
    public Task<string> RequestStorageShare() => _protocol.RequestModule(Names.StorageShare);
    public Task<string> RequestReboot() => _protocol.RequestModule(Names.Reboot);
    public Task<string> RequestRestoreDefaults() => _protocol.RequestModule(Names.RestoreConf);
    public Task<string> RequestTime() => _protocol.RequestModule(Names.Time);
    public Task<string> RequestLog() => _protocol.RequestModule(Names.Log);
    public Task<string> RequestAPBridge() => _protocol.RequestModule(Names.APBridge);
    public Task<string> RequestVoice() => _protocol.RequestModule(Names.Voice);
    public Task<string> RequestUPnP() => _protocol.RequestModule(Names.UPnP);
    public Task<string> RequestDMZ() => _protocol.RequestModule(Names.DMZ);
    public Task<string> RequestALG() => _protocol.RequestModule(Names.ALG);
    public Task<string> RequestVirtualServer() => _protocol.RequestModule(Names.VirtualServer);
    public Task<string> RequestPortTriggering() => _protocol.RequestModule(Names.PortTriggering);
}
