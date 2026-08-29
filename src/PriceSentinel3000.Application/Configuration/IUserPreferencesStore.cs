using PriceSentinel3000.Core.Configuration;

namespace PriceSentinel3000.Application.Configuration;

public interface IUserPreferencesStore
{
    TradingSessionSettings? Load();

    bool Save(TradingSessionSettings settings);
}
