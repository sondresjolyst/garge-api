using garge_api.Models.Admin;
using garge_api.Models.Auth;
using garge_api.Models.Automation;
using garge_api.Models.Electricity;
using garge_api.Models.Group;
using garge_api.Models.Mqtt;
using garge_api.Models.Push;
using garge_api.Models.Sensor;
using garge_api.Models.Shop;
using garge_api.Models.Subscription;
using garge_api.Models.Switch;
using garge_api.Models.Webhook;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace garge_api.Models
{
    public partial class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : IdentityDbContext<User, IdentityRole, string>(options), IDataProtectionKeyContext
    {
        public DbSet<DataProtectionKey> DataProtectionKeys { get; set; }
        public DbSet<UserProfile> UserProfiles { get; set; }
        public DbSet<RolePermission> RolePermissions { get; set; }
        public DbSet<Sensor.Sensor> Sensors { get; set; }
        public DbSet<SensorData> SensorData { get; set; }
        public DbSet<BatteryHealth> BatteryHealthData { get; set; }
        public DbSet<BatteryChargeEvent> BatteryChargeEvents { get; set; }
        public DbSet<Switch.Switch> Switches { get; set; }
        public DbSet<SwitchData> SwitchData { get; set; }
        public DbSet<RefreshToken> RefreshTokens { get; set; }
        public DbSet<UserSensorCustomName> UserSensorCustomNames { get; set; }
        public DbSet<SensorActivity> SensorActivities { get; set; }
        public DbSet<EMQXMqttUser> EMQXMqttUsers { get; set; }
        public DbSet<EMQXMqttAcl> EMQXMqttAcls { get; set; }
        public DbSet<DiscoveredDevice> DiscoveredDevices { get; set; }
        public DbSet<AutomationRule> AutomationRules { get; set; }
        public DbSet<Group.Group> Groups { get; set; }
        public DbSet<GroupSensor> GroupSensors { get; set; }
        public DbSet<GroupSwitch> GroupSwitches { get; set; }
        public DbSet<UserSwitchCustomName> UserSwitchCustomNames { get; set; }
        public DbSet<UserSensor> UserSensors { get; set; }
        public DbSet<SensorOwnershipPeriod> SensorOwnershipPeriods { get; set; }
        public DbSet<UserSwitch> UserSwitches { get; set; }
        public DbSet<SwitchOwnershipPeriod> SwitchOwnershipPeriods { get; set; }
        public DbSet<StoredElectricityPrice> StoredElectricityPrices { get; set; }
        public DbSet<SensorPhoto> SensorPhotos { get; set; }
        public DbSet<PushSubscription> PushSubscriptions { get; set; }
        public DbSet<SensorOfflineNotification> SensorOfflineNotifications { get; set; }
        public DbSet<AppSettings> AppSettings { get; set; }
        public DbSet<Product> Products { get; set; }
        public DbSet<Subscription.Subscription> Subscriptions { get; set; }
        public DbSet<ShopItem> ShopItems { get; set; }
        public DbSet<ShopItemPhoto> ShopItemPhotos { get; set; }
        public DbSet<Order> Orders { get; set; }
        public DbSet<OrderItem> OrderItems { get; set; }
        public DbSet<Invoice> Invoices { get; set; }
        public DbSet<ProcessedWebhookEvent> ProcessedWebhookEvents { get; set; }
        public DbSet<Anonymized.AnonymizedSeries> AnonymizedSeries { get; set; }
        public DbSet<Anonymized.AnonymizedReading> AnonymizedReadings { get; set; }

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

            modelBuilder.Entity<BatteryChargeEvent>()
                .HasIndex(e => new { e.SensorId, e.StartedAt })
                .IsUnique();

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

            modelBuilder.Entity<SensorActivity>()
                .HasOne(sa => sa.Sensor)
                .WithMany()
                .HasForeignKey(sa => sa.SensorId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<SensorActivity>()
                .HasIndex(sa => sa.SensorId);

            modelBuilder.Entity<SensorActivity>()
                .HasIndex(sa => sa.ActivityDate);

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

            modelBuilder.Entity<SensorOwnershipPeriod>()
                .HasIndex(p => new { p.UserId, p.SensorId });

            modelBuilder.Entity<SensorOwnershipPeriod>()
                .HasIndex(p => new { p.SensorId, p.StartedAt });

            modelBuilder.Entity<SensorOwnershipPeriod>()
                .HasOne(p => p.User)
                .WithMany()
                .HasForeignKey(p => p.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<SensorOwnershipPeriod>()
                .HasOne(p => p.Sensor)
                .WithMany()
                .HasForeignKey(p => p.SensorId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<UserSwitch>()
                .HasKey(x => new { x.UserId, x.SwitchId });

            modelBuilder.Entity<SwitchOwnershipPeriod>()
                .HasIndex(p => new { p.UserId, p.SwitchId });

            modelBuilder.Entity<SwitchOwnershipPeriod>()
                .HasIndex(p => new { p.SwitchId, p.StartedAt });

            modelBuilder.Entity<SwitchOwnershipPeriod>()
                .HasOne(p => p.User)
                .WithMany()
                .HasForeignKey(p => p.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<SwitchOwnershipPeriod>()
                .HasOne(p => p.Switch)
                .WithMany()
                .HasForeignKey(p => p.SwitchId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<Anonymized.AnonymizedReading>()
                .HasIndex(r => new { r.SeriesId, r.Timestamp });

            modelBuilder.Entity<Anonymized.AnonymizedReading>()
                .HasOne(r => r.Series)
                .WithMany()
                .HasForeignKey(r => r.SeriesId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<UserSwitch>()
                .HasOne(x => x.User)
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<UserSwitch>()
                .HasOne(x => x.Switch)
                .WithMany()
                .HasForeignKey(x => x.SwitchId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<StoredElectricityPrice>()
                .HasIndex(p => new { p.Area, p.Resolution, p.DeliveryStart })
                .IsUnique();

            modelBuilder.Entity<SensorPhoto>()
                .HasOne(sp => sp.Sensor)
                .WithMany()
                .HasForeignKey(sp => sp.SensorId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<SensorPhoto>()
                .HasOne(sp => sp.User)
                .WithMany()
                .HasForeignKey(sp => sp.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<SensorPhoto>()
                .HasIndex(sp => sp.SensorId)
                .IsUnique();

            modelBuilder.Entity<PushSubscription>()
                .HasIndex(ps => new { ps.UserId, ps.Endpoint })
                .IsUnique();

            modelBuilder.Entity<SensorOfflineNotification>()
                .HasIndex(n => new { n.UserId, n.SensorId, n.ResolvedAt });

            modelBuilder.Entity<AppSettings>()
                .Property(s => s.Id)
                .ValueGeneratedNever();

            modelBuilder.Entity<AppSettings>()
                .ToTable(t => t.HasCheckConstraint("CK_AppSettings_SingleRow", "\"Id\" = 1"));

            modelBuilder.Entity<AppSettings>()
                .HasData(new AppSettings());

            modelBuilder.Entity<Product>()
                .HasIndex(p => p.IsActive);

            modelBuilder.Entity<Subscription.Subscription>()
                .HasOne(s => s.User)
                .WithMany()
                .HasForeignKey(s => s.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<Subscription.Subscription>()
                .HasOne(s => s.Product)
                .WithMany()
                .HasForeignKey(s => s.ProductId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<Subscription.Subscription>()
                .HasIndex(s => s.VippsAgreementId)
                .IsUnique();

            modelBuilder.Entity<Subscription.Subscription>()
                .HasIndex(s => new { s.UserId, s.Status });

            modelBuilder.Entity<ShopItem>()
                .HasIndex(si => si.IsActive);

            modelBuilder.Entity<ShopItemPhoto>()
                .HasOne(sip => sip.ShopItem)
                .WithMany()
                .HasForeignKey(sip => sip.ShopItemId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<ShopItemPhoto>()
                .HasOne(sip => sip.User)
                .WithMany()
                .HasForeignKey(sip => sip.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<ShopItemPhoto>()
                .HasIndex(sip => sip.ShopItemId)
                .IsUnique();

            modelBuilder.Entity<Order>()
                .HasOne(o => o.User)
                .WithMany()
                .HasForeignKey(o => o.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<Order>()
                .HasIndex(o => o.VippsOrderId)
                .IsUnique()
                .HasFilter("\"VippsOrderId\" IS NOT NULL");

            modelBuilder.Entity<Order>()
                .HasIndex(o => o.UserId);

            modelBuilder.Entity<OrderItem>()
                .HasOne(oi => oi.Order)
                .WithMany(o => o.OrderItems)
                .HasForeignKey(oi => oi.OrderId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<OrderItem>()
                .HasOne(oi => oi.ShopItem)
                .WithMany()
                .HasForeignKey(oi => oi.ShopItemId)
                .OnDelete(DeleteBehavior.Restrict);

            modelBuilder.Entity<Invoice>()
                .HasOne(i => i.Order)
                .WithOne(o => o.Invoice)
                .HasForeignKey<Invoice>(i => i.OrderId)
                .OnDelete(DeleteBehavior.Cascade);

            // Filtered unique index: one invoice per order when set; multiple rows
            // with NULL OrderId are allowed (subscription invoices).
            modelBuilder.Entity<Invoice>()
                .HasIndex(i => i.OrderId)
                .IsUnique()
                .HasFilter("\"OrderId\" IS NOT NULL");

            modelBuilder.Entity<Invoice>()
                .HasOne(i => i.Subscription)
                .WithMany()
                .HasForeignKey(i => i.SubscriptionId)
                .OnDelete(DeleteBehavior.Cascade);

            // Unique-when-present index so webhook redelivery can't produce duplicate
            // invoices for the same Vipps charge.
            modelBuilder.Entity<Invoice>()
                .HasIndex(i => i.VippsChargeId)
                .IsUnique()
                .HasFilter("\"VippsChargeId\" IS NOT NULL");

            modelBuilder.Entity<ProcessedWebhookEvent>()
                .HasIndex(p => new { p.Source, p.Id });

            modelBuilder.Entity<ProcessedWebhookEvent>()
                .HasIndex(p => p.ProcessedAt);
        }
        public void EnsureTriggers()
        {
            var switchTriggerFunctionSql = @"
        CREATE OR REPLACE FUNCTION notify_switchdata_change()
        RETURNS TRIGGER AS $$
        BEGIN
            PERFORM pg_notify('switchdata_channel', row_to_json(NEW)::text);
            RETURN NEW;
        END;
        $$ LANGUAGE plpgsql;";

            var switchTriggerSql = @"
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

            var sensorTriggerFunctionSql = @"
        CREATE OR REPLACE FUNCTION notify_sensordata_change()
        RETURNS TRIGGER AS $$
        BEGIN
            PERFORM pg_notify('sensordata_channel', row_to_json(NEW)::text);
            RETURN NEW;
        END;
        $$ LANGUAGE plpgsql;";

            var sensorTriggerSql = @"
        DO $$
        BEGIN
            IF NOT EXISTS (
                SELECT 1
                FROM pg_trigger
                WHERE tgname = 'sensordata_change_trigger'
            ) THEN
                CREATE TRIGGER sensordata_change_trigger
                AFTER INSERT ON ""SensorData""
                FOR EACH ROW EXECUTE FUNCTION notify_sensordata_change();
            END IF;
        END $$;";

            Database.ExecuteSqlRaw(switchTriggerFunctionSql);
            Database.ExecuteSqlRaw(switchTriggerSql);
            Database.ExecuteSqlRaw(sensorTriggerFunctionSql);
            Database.ExecuteSqlRaw(sensorTriggerSql);
        }

        partial void OnModelCreatingPartial(ModelBuilder modelBuilder);
    }
}
