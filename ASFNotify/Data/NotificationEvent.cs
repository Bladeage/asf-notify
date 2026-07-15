namespace ASFNotify.Data;

// Enum names double as the config "Events" values (matched case-insensitively).
internal enum EEventType : byte {
	LoggedOn,
	LoginAttention,
	Disconnected,
	FarmingStarted,
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

// BotName is a synthetic label ("ASF") for server-scoped events that carry no bot.
internal sealed record NotificationEvent(string BotName, EEventType Type, string Title, string Message, ENotificationPriority Priority);
