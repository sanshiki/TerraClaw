using System.IO;
using Terraria;

namespace TerraClaw.Util
{
    public struct ProjectileReference
    {
        public int Identity;
        public int WhoAmI;
        public int Owner;

        public void Clear()
        {
            Identity = -1;
            WhoAmI = -1;
            Owner = -1;
        }

        public ProjectileReference(Projectile projectile)
        {
            Set(projectile);
        }

        public void Set(Projectile projectile)
        {
            Identity = projectile.identity;
            WhoAmI = projectile.whoAmI;
            Owner = projectile.owner;
        }

        public bool IsValid()
        {
            return Identity >= 0 && WhoAmI >= 0;
        }

        public Projectile? Get()
        {
            if (WhoAmI >= 0 && WhoAmI < Main.maxProjectiles)
            {
                Projectile cached = Main.projectile[WhoAmI];

                if (cached.active && cached.identity == Identity && cached.owner == Owner)
                {
                    return cached;
                }
            }

            for (int i = 0; i < Main.maxProjectiles; i++)
            {
                Projectile proj = Main.projectile[i];

                if (proj.active && proj.identity == Identity && proj.owner == Owner)
                {
                    WhoAmI = i;
                    return proj;
                }
            }

            return null;
        }

        public void SendExtraAI(BinaryWriter writer)
        {
            writer.Write(Identity);
            writer.Write(WhoAmI);
            writer.Write(Owner);
        }

        public void ReceiveExtraAI(BinaryReader reader)
        {
            Identity = reader.ReadInt32();
            WhoAmI = reader.ReadInt32();
            Owner = reader.ReadInt32();
        }
    }
}
