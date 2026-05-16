using Mapster;
using garge_api.Dtos.Admin;
using garge_api.Dtos.Auth;
using garge_api.Dtos.Electricity;
using garge_api.Dtos.Sensor;
using garge_api.Dtos.Shop;
using garge_api.Dtos.Subscription;
using garge_api.Dtos.Switch;
using garge_api.Dtos.User;
using garge_api.Models.Admin;
using garge_api.Models.Electricity;
using garge_api.Models.Sensor;
using garge_api.Models.Shop;
using garge_api.Models.Subscription;
using garge_api.Models.Switch;
using Microsoft.AspNetCore.Identity;

public class MappingProfile : IRegister
{
    public void Register(TypeAdapterConfig config)
    {
        // Admin mappings
        config.NewConfig<IdentityRole, RoleDto>();
        config.NewConfig<RolePermission, RolePermissionDto>().TwoWays();
        config.NewConfig<User, UserDto>();
        config.NewConfig<AppSettings, AppSettingsDto>();
        config.NewConfig<AppSettings, PublicSettingsDto>();
        config.NewConfig<UpdateAppSettingsDto, AppSettings>()
            .IgnoreNullValues(true);

        // Electricity mappings
        config.NewConfig<PriceResponse, PriceResponseDto>();
        config.NewConfig<AreaPrices, AreaPricesDto>();
        config.NewConfig<PriceEntry, PriceEntryDto>();

        // Sensor mappings
        config.NewConfig<CreateSensorDataDto, SensorData>();
        config.NewConfig<CreateSensorDto, Sensor>();
        config.NewConfig<Sensor, SensorDto>().TwoWays();
        config.NewConfig<UpdateSensorDto, Sensor>();
        config.NewConfig<SensorData, SensorDataDto>().TwoWays();
        config.NewConfig<BatteryHealth, BatteryHealthDto>().TwoWays();
        config.NewConfig<CreateBatteryHealthDto, BatteryHealth>();
        config.NewConfig<BatteryChargeEvent, BatteryChargeEventDto>();
        config.NewConfig<SensorActivity, SensorActivityDto>().TwoWays();
        config.NewConfig<CreateSensorActivityDto, SensorActivity>();
        config.NewConfig<UpdateSensorActivityDto, SensorActivity>();

        // Switch mappings
        config.NewConfig<Switch, SwitchDto>().TwoWays();
        config.NewConfig<CreateSwitchDto, Switch>();
        config.NewConfig<UpdateSwitchDto, Switch>();
        config.NewConfig<SwitchData, SwitchDataDto>().TwoWays();
        config.NewConfig<CreateSwitchDataDto, SwitchData>();

        // User mappings
        config.NewConfig<UserProfile, UserProfileDto>()
            .Map(d => d.FirstName, s => s.User.FirstName)
            .Map(d => d.LastName, s => s.User.LastName)
            .Map(d => d.Email, s => s.User.Email)
            .Map(d => d.PhoneNumber, s => s.User.PhoneNumber)
            .Map(d => d.EmailConfirmed, s => s.User.EmailConfirmed);
        config.NewConfig<RegisterUserDto, User>();

        // Subscription plan mappings
        config.NewConfig<Product, ProductResponseDto>()
            .Map(d => d.Interval, s => s.Interval.ToString())
            .Map(d => d.Type, s => s.Type.ToString());
        config.NewConfig<CreateProductDto, Product>();
        config.NewConfig<UpdateProductDto, Product>();

        config.NewConfig<garge_api.Models.Subscription.Subscription, SubscriptionResponseDto>()
            .Map(d => d.Status, s => s.Status.ToString())
            .Map(d => d.Interval, s => s.Product != null ? s.Product.Interval.ToString() : string.Empty)
            .Map(d => d.ProductName, s => s.Product != null ? s.Product.Name : string.Empty)
            .Map(d => d.ProductType, s => s.Product != null ? s.Product.Type.ToString() : string.Empty)
            .Map(d => d.PriceInOre, s => s.Product != null ? s.Product.PriceInOre : 0)
            .Map(d => d.Quantity, s => s.Quantity);

        // Shop mappings
        config.NewConfig<ShopItem, ShopItemResponseDto>();
        config.NewConfig<CreateShopItemDto, ShopItem>();
        config.NewConfig<UpdateShopItemDto, ShopItem>();

        config.NewConfig<Order, OrderResponseDto>()
            .Map(d => d.Status, s => s.Status.ToString())
            .Map(d => d.Items, s => s.OrderItems)
            .Map(d => d.HasInvoice, s => s.Invoice != null);

        config.NewConfig<OrderItem, OrderItemResponseDto>()
            .Map(d => d.ShopItemName, s => s.ShopItem != null ? s.ShopItem.Name : string.Empty);

        config.NewConfig<Invoice, InvoiceResponseDto>();

        config.NewConfig<Order, AdminOrderResponseDto>()
            .Map(d => d.Status, s => s.Status.ToString())
            .Map(d => d.Items, s => s.OrderItems)
            .Map(d => d.HasInvoice, s => s.Invoice != null)
            .Map(d => d.UserEmail, s => s.User != null ? s.User.Email ?? string.Empty : string.Empty)
            .Map(d => d.UserName, s => s.User != null ? s.User.FirstName + " " + s.User.LastName : string.Empty);
    }
}
