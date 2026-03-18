using TenCrowns.GameCore;
using TenCrowns.GameCore.Text;

namespace OldWorldMapGen
{
    /// <summary>
    /// GameFactory override that skips ColorManager creation, which requires
    /// Unity's native Mathf.GammaToLinearSpace via Color.get_linear.
    /// </summary>
    public class HeadlessGameFactory : GameFactory
    {
        public override ColorManager CreateColorManager(Infos pInfos, TextManager textManager)
        {
            return null;
        }
    }
}
