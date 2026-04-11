using garge_api.Models.Admin;
using garge_api.Models.Auth;
using garge_api.Models.Automation;
using garge_api.Models.Group;
using garge_api.Models.Mqtt;
using garge_api.Models.Sensor;
using garge_api.Models.Switch;
using garge_api.Models.Webhook;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace garge_api.Models
{
    public partial class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : IdentityDbContext<User, IdentityRole, string>(options)
    {
        public DbSet<UserProfile> UserProfiles { get; set; }
        public DbSet<RolePermission> RolePermissions { get; set; }
        public DbSet<Sensor.Sensor> Sensors { get; set; }
        public DbSet<SensorData> SensorData { get; set; }
        public DbSet<BatteryHealth> BatteryHealthData { get; set; }
        public DbSet<Switch.Switch> Switches { get; set; }
        public DbSet<SwitchData> SwitchData { get; set; }
        public DbSet<WebhookSubscription> WebhookSubscriptions { get; set; }
        public DbSet<RefreshToken> RefreshTokens { get; set; }
        public DbSet<UserSensorCustomName> UserSensorCustomNames { get; set; }
        public DbSet<EMQXMqttUser> EMQXMqttUsers { get; set; }
        public DbSet<EMQXMqttAcl> EMQXMqttAcls { get; set; }
        public DbSet<DiscoveredDevice> DiscoveredDevices { get; set; }
        public DbSet<AutomationRule> AutomationRules { get; set; }
        public DbSet<Group.Group> Groups { get; set; }
        public DbSet<GroupSensor> GroupSensors { get; set; }
        public DbSet<GroupSwitch> GroupSwitches { get; set; }
        public DbSet<UserSwitchCustomName> UserSwitchCustomNames { get; set; }
        public DbSet<UserSensor> UserSensors { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<UserProfile>()
                .HasOne(up => up.User)
                .WithOne()
                .HasForeignKey<UserProfile>(up => up.Id);

            modelBuilder.Entity<RolePermission>()
                .HasIndex(rp => new { rp.RoleName, rp.Permission })
                .IsUnique();

            modelBuilder.Entity<Sensor.Sensor>()
                .HasIndex(s => s.Name)
                .IsUnique();

            modelBuilder.Entity<SensorData>()
                .HasIndex(sd => sd.SensorId);

            modelBuilder.Entity<SensorData>()
                .HasIndex(sd => sd.Timestamp);

            modelBuilder.Entity<SensorData>()
                .HasIndex(sd => new { sd.SensorId, sd.Timestamp })
                .HasDatabaseName("IX_SensorData_SensorId_Timestamp");

            modelBuilder.Entity<BatteryHealth>()
                .HasIndex(bh => bh.SensorId);

            modelBuilder.Entity<BatteryHealth>()
                .HasIndex(bh => bh.Timestamp);

            modelBuilder.Entity<SwitchData>()
                .ToTable("SwitchData");

            modelBuilder.Entity<UserSensorCustomName>()
                .HasKey(x => new { x.UserId, x.SensorId });

            modelBuilder.Entity<UserSensorCustomName>()
                .HasOne(x => x.User)
                .WithMany()
                .HasForeignKey(x => x.UserId);

            modelBuilder.Entity<UserSensorCustomName>()
                .HasOne(x => x.Sensor)
                .WithMany()
                .HasForeignKey(x => x.SensorId);

            OnModelCreatingPartial(modelBuilder);

            modelBuilder.Entity<AutomationRule>()
                .HasIndex(ar => new
                {
                    ar.TargetType,
                    ar.TargetId,
                    ar.SensorType,
                    ar.SensorId,
                    ar.Condition,
                    ar.Threshold,
                    ar.Action
                })
                .IsUnique();

            modelBuilder.Entity<GroupSensor>()
                .HasKey(gs => new { gs.GroupId, gs.SensorId });

            modelBuilder.Entity<GroupSensor>()
                .HasOne(gs => gs.Group)
                .WithMany(g => g.GroupSensors)
                .HasForeignKey(gs => gs.GroupId);

            modelBuilder.Entity<GroupSensor>()
                .HasOne(gs => gs.Sensor)
                .WithMany()
                .HasForeignKey(gs => gs.SensorId);

            modelBuilder.Entity<GroupSwitch>()
                .HasKey(gs => new { gs.GroupId, gs.SwitchId });

            modelBuilder.Entity<GroupSwitch>()
                .HasOne(gs => gs.Group)
                .WithMany(g => g.GroupSwitches)
                .HasForeignKey(gs => gs.GroupId);

            modelBuilder.Entity<GroupSwitch>()
                .HasOne(gs => gs.Switch)
                .WithMany()
                .HasForeignKey(gs => gs.SwitchId);

            modelBuilder.Entity<UserSwitchCustomName>()
                .HasKey(x => new { x.UserId, x.SwitchId });

            modelBuilder.Entity<UserSwitchCustomName>()
                .HasOne(x => x.User)
                .WithMany()
                .HasForeignKey(x => x.UserId);

            modelBuilder.Entity<UserSwitchCustomName>()
                .HasOne(x => x.Switch)
                .WithMany()
                .HasForeignKey(x => x.SwitchId);

            modelBuilder.Entity<UserSensor>()
                .HasKey(x => new { x.UserId, x.SensorId });

            modelBuilder.Entity<UserSensor>()
                .HasOne(x => x.User)
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<UserSensor>()
                .HasOne(x => x.Sensor)
                .WithMany()
                .HasForeignKey(x => x.SensorId)
                .OnDelete(DeleteBehavior.Cascade);
        }
        public void EnsureTriggers()
        {
            var triggerFunctionSql = @"
        CREATE OR REPLACE FUNCTION notify_switchdata_change()
        RETURNS TRIGGER AS $$
        BEGIN
            PERFORM pg_notify('switchdata_channel', row_to_json(NEW)::text);
            RETURN NEW;
        END;
        $$ LANGUAGE plpgsql;";

            var triggerSql = @"
        DO $$
        BEGIN
            IF NOT EXISTS (
                SELECT 1
                FROM pg_trigger
                WHERE tgname = 'switchdata_change_trigger'
            ) THEN
                CREATE TRIGGER switchdata_change_trigger
                AFTER INSERT ON ""SwitchData""
                FOR EACH ROW EXECUTE FUNCTION notify_switchdata_change();
            END IF;
        END $$;";

            Database.ExecuteSqlRaw(triggerFunctionSql);
            Database.ExecuteSqlRaw(triggerSql);
        }

        partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
    }
}
