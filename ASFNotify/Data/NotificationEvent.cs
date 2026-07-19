namespace ASFNotify.Data;

// Enum names double as the config "Events" values (matched case-insensitively).
internal enum EEventType : byte {
	LoggedOn,
	LoginAttention,
	Disconnected,
	GameFarmingStarted,
	GameFarmingFinished,
	MassFarmingStarted,
	FarmingFinished,
	FarmingStopped,
	TradeOffer,
	TradeAccepted,
	TradeRefused,
	GiftReceived,
	AccountAlert,
	GameRedeemed,
	BotAdded,
	BotRemoved,
	AsfStarted,
	AsfUpdated,
	PluginUpdated
}

internal enum ENotificationPriority : byte {
	Low,
	Normal,
	High
}

// BotName is a synthetic label ("ASF") for server-scoped events that carry no bot. BypassCooldown is
// for events that deduplicate themselves (per-game farming events announce each game once per session
// and aggregate); the generic per-(bot,event) cooldown would silently drop distinct games otherwise.
internal sealed record NotificationEvent(string BotName, EEventType Type, string Title, string Message, ENotificationPriority Priority, bool BypassCooldown = false);
