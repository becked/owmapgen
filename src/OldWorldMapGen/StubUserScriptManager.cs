using TenCrowns.AppCore;
using TenCrowns.GameCore;

namespace OldWorldMapGen
{
    public class StubUserScriptManager : UserScriptManagerBase
    {
        public override void Initialize(ModSettings modSettings)
        {
            modSettings.Factory = new HeadlessGameFactory();
        }

        public override void Shutdown() { }
        public override void OnClientUpdate() { }
        public override void OnClientPostUpdate() { }
        public override void OnServerUpdate() { }
        public override void OnNewTurnServer() { }
        public override bool CallOnGUI() => false;
        public override void OnGUI() { }
        public override void OnPreGameServer() { }
        public override void OnGameServerReady() { }
        public override void OnGameClientReady() { }
        public override void OnPreLoad() { }
        public override void OnPostLoad() { }
        public override void OnRendererReady() { }
        public override void OnGameOver() { }
        public override void OnExitGame() { }
    }
}
