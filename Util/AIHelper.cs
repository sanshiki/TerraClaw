using Microsoft.Xna.Framework;
using System;
using Terraria;
using Terraria.ModLoader;
using Microsoft.Xna.Framework.Graphics;
using Terraria.GameContent;
using System.Collections.Generic;
using Terraria.ID;
using System.Linq;
using Terraria.WorldBuilding;
using Terraria.DataStructures;

namespace TerraClaw.Util
{
	/// <summary>
	/// 召唤物AI通用工具类
	/// </summary>
	public static class AIHelper
	{
		#region Constants
		// 默认距离常量
		public const float DEFAULT_TELEPORT_DISTANCE = 2500f;
		public const float DEFAULT_TARGET_SEARCH_RANGE = 700f;
		public const float DEFAULT_MAX_TARGET_DISTANCE = 2000f;
		public const float DEFAULT_CLOSE_THROUGH_WALL_DISTANCE = 100f;
		public const float DEFAULT_MINION_SPACING = 40f;
		public const float DEFAULT_OVERLAP_VELOCITY = 0.04f;
		#endregion

		#region Target Search
		/// <summary>
		/// 搜索目标的结果
		/// </summary>
		public struct TargetSearchResult
		{
			public bool FoundTarget;
			public float DistanceFromTarget;
			public Vector2 TargetCenter;
			public NPC TargetNPC;

			public TargetSearchResult(bool foundTarget, float distance, Vector2 center, NPC npc = null)
			{
				FoundTarget = foundTarget;
				DistanceFromTarget = distance;
				TargetCenter = center;
				TargetNPC = npc;
			}
		}

		/// <summary>
		/// 搜索目标
		/// </summary>
		/// <param name="owner">召唤物主人</param>
		/// <param name="projectile">召唤物</param>
		/// <param name="searchRange">搜索范围</param>
		/// <param name="maxDistance">最大目标距离</param>
		/// <returns>搜索结果</returns>
		public static TargetSearchResult SearchForTargets(Player owner, Projectile minion, 
			float searchRange = DEFAULT_TARGET_SEARCH_RANGE, 
			bool checkCanHit = true,
			Func<NPC, bool> otherCondition = null,
			bool checkAttackTarget = true)
		{
			float distanceFromTarget = searchRange;
			Vector2 targetCenter = minion.position;
			bool foundTarget = false;
			NPC targetNPC = null;

			// 检查玩家指定的目标
			if (checkAttackTarget && owner.HasMinionAttackTargetNPC)
			{
				NPC npc = Main.npc[owner.MinionAttackTargetNPC];
				float distance = Vector2.Distance(npc.Center, minion.Center);
				bool canBeChased = npc.CanBeChasedBy(minion);
				bool canHit = checkCanHit ? Collision.CanHitLine(minion.position, /* minion.width, minion.height,  */ 6, 6, 
																  npc.position, npc.width, npc.height) : true;

				if (distance < searchRange && canHit && canBeChased && (otherCondition == null || otherCondition(npc)))
				{
					distanceFromTarget = distance;
					targetCenter = npc.Center;
					foundTarget = true;
					targetNPC = npc;
				}
			}

			// 搜索附近的目标
			if (!foundTarget)
			{
				foreach (var npc in Main.ActiveNPCs)
				{
					bool canBeChased = npc.CanBeChasedBy(minion);
					bool canHit = checkCanHit ? Collision.CanHitLine(minion.position, minion.width, minion.height, 
																  npc.position, npc.width, npc.height) : true;
					if (canBeChased && canHit && (otherCondition == null || otherCondition(npc)))
					{
						float distance = Vector2.Distance(npc.Center, minion.Center);
						// bool hasSentryTargetTag = npc.HasBuff(ModBuffID.SentryTarget);
						bool hasSentryTargetTag = false;	// temporarily disabled
						if (distance < distanceFromTarget || hasSentryTargetTag)
						{
							distanceFromTarget = distance;
							targetCenter = npc.Center;
							foundTarget = true;
							targetNPC = npc;
						}

						if (hasSentryTargetTag) break;
					}
				}
			}

			return new TargetSearchResult(foundTarget, distanceFromTarget, targetCenter, targetNPC);
		}

		public static List<int> SearchForProjectiles(int type, Vector2 center, float radius)
		{
			List<int> projectileIDs = new List<int>();
			for(int i = 0; i < Main.maxProjectiles; i++)
			{
				Projectile projectile = Main.projectile[i];
				if(projectile.type == type && projectile.active)
				{
					if(projectile.Center.Distance(center) < radius)
					{
						projectileIDs.Add(i);
					}
				}
			}
			return projectileIDs;
		}

		public static List<NPC> SearchTargetsInRadius(Vector2 center, float radius)
		{
			List<NPC> targets = new List<NPC>();
			foreach (NPC npc in Main.npc)
            {
                if (npc.active && !npc.friendly && npc.Distance(center) < radius && !npc.dontTakeDamage && !npc.immortal)
                {
                    targets.Add(npc);
                }
            }
			return targets;
		}

		/// <summary>
		/// 更新召唤物的友好状态
		/// </summary>
		/// <param name="projectile">召唤物</param>
		/// <param name="hasTarget">是否有目标</param>
		public static void UpdateFriendlyState(Projectile projectile, bool hasTarget)
		{
			projectile.friendly = hasTarget;
		}
		#endregion

		#region Prediction Methods
		/// <summary>
		/// 预测目标位置
		/// </summary>
		/// <param name="projectile">发射物</param>
		/// <param name="target">目标</param>
		/// <param name="bulletSpeed">子弹速度</param>
		/// <param name="accelerationFactor">加速度因子</param>
		/// <param name="maxPredictionTicks">最大预测帧数</param>
		/// <param name="tickStep">预测步长</param>
		/// <returns>预测位置</returns>
		public static Vector2 PredictTargetPosition(Projectile projectile, NPC target, float bulletSpeed, int maxPredictionTicks = 60, int tickStep = 3)
		{
			return PredictTargetPosition(projectile.Center, target.Center, target.velocity, bulletSpeed, maxPredictionTicks, tickStep);
		}

		public static Vector2 PredictTargetPosition(Vector2 projectileCenter, Vector2 targetCenter, Vector2 targetVelocity, float bulletSpeed, int maxPredictionTicks = 60, int tickStep = 3)
		{
			Vector2 predictedPos = targetCenter;
			
			for (int tick = 0; tick < maxPredictionTicks; tick += tickStep)
			{
				Vector2 targetPredictedPos = targetCenter + targetVelocity * tick;

				if(Collision.SolidCollision(targetPredictedPos, 1, 1)) continue;

				predictedPos = targetPredictedPos;

				float bulletFlyTime = Vector2.Distance(projectileCenter, targetPredictedPos) / bulletSpeed;

				if (bulletFlyTime < tick)
				{
					break;
				}
			}

			return predictedPos;
		}

		public static Vector2 PredictVelocityWithGravity(Vector2 projectileCenter, Vector2 targetCenter, Vector2 targetVelocity, float bulletSpeedY, float gravity = 0.4f, int maxPredictionTicks = 60, int tickStep = 3)
		{
			Vector2 predictedVel = new Vector2(0, -bulletSpeedY);
			for (int tick = 0; tick < maxPredictionTicks; tick += tickStep)
			{
				Vector2 targetPredictedPos = targetCenter + targetVelocity * tick;

				Vector2 direction = targetPredictedPos - projectileCenter;

				if(Collision.SolidCollision(targetPredictedPos, 1, 1)) continue;

				float vy = bulletSpeedY;
				float bullet_gravity = gravity;
				float delta = Math.Max(0, vy * vy + 2 * bullet_gravity * direction.Y);
				float pred_t1 = (vy + (float)Math.Sqrt(delta)) / bullet_gravity;
				float pred_t2 = (vy - (float)Math.Sqrt(delta)) / bullet_gravity;
				float bulletFlyTime = Math.Max(pred_t1, pred_t2);

				// Main.NewText("Tick:" + tick.ToString() + " bulletFlyTime:" + bulletFlyTime.ToString());

				float vx = direction.X / bulletFlyTime;
				predictedVel = new Vector2(vx, -vy);

				if (bulletFlyTime < tick)
				{
					break;
				}
			}

			return predictedVel;
		}
		#endregion

		#region Random Methods
		public static float RandomFloat(float min, float max)
		{
			return (float)Main.rand.NextDouble() * (max - min) + min;
		}
		public static float RandomFloat(double min, double max)
		{
			return (float)(Main.rand.NextDouble() * (max - min) + min);
		}
		public static float RandomFloat(float min, double max)
		{
			return (float)Main.rand.NextDouble() * ((float)max - min) + min;
		}
		public static float RandomFloat(double min, float max)
		{
			return (float)Main.rand.NextDouble() * (max - (float)min) + (float)min;
		}
		public static bool RandomBool()
		{
			return Main.rand.Next(2) == 1;
		}
		public static float RandomSign()
		{
			return Main.rand.Next(2) == 1 ? 1 : -1;
		}
		public static int RandomInt(int min, int max)
		{
			return Main.rand.Next(min, max);
		}
		#endregion

		#region Dust Methods
		public static void GenerateLightning(Vector2 start, Vector2 end, float displacement, float minDisplacement, List<Vector2> points)
		{
			if (displacement < minDisplacement)
			{
				points.Add(start);
				points.Add(end);
			}
			else
			{
				Vector2 mid = (start + end) / 2;
				// 垂直方向
				Vector2 dir = new Vector2(end.Y - start.Y, start.X - end.X);
				dir.Normalize();
				// 随机偏移
				mid += dir * (RandomFloat(-displacement, displacement));

				// 递归生成
				GenerateLightning(start, mid, displacement / 2, minDisplacement, points);
				GenerateLightning(mid, end, displacement / 2, minDisplacement, points);
			}
		}
		#endregion

		#region Homein Methods
		public static void HomeinToTarget(Projectile projectile, Vector2 target, float speed, float inertia)
		{
			// Vector2 direction = target - projectile.Center;
			// direction.Normalize();
			// direction *= speed;
			// projectile.velocity = (projectile.velocity * (inertia - 1) + direction) / inertia;

			// Main.NewText("Homein velocity: " + projectile.velocity + " direction: " + direction + " speed: " + speed + " inertia: " + inertia);
			projectile.velocity = HomeinToTarget(projectile.Center, projectile.velocity, target, speed, inertia);
		}

		public static Vector2 HomeinToTarget(Vector2 center, Vector2 vel, Vector2 target, float speed, float inertia)
		{
			Vector2 direction = target - center;
			direction.Normalize();
			direction *= speed;
			return (vel * (inertia - 1) + direction) / inertia;
		}
		#endregion
	}
} 