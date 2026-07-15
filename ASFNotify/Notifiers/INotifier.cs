using System.Threading;
using System.Threading.Tasks;
using ASFNotify.Configuration;
using ASFNotify.Data;

namespace ASFNotify.Notifiers;

// A delivery backend. Implementations are stateless; all state comes from the config.
internal interface INotifier {
	string Name { get; }

	bool IsConfigured(PluginConfig config);

	// Delivers one notification. Returns true on success and should not throw on expected failures.
	Task<bool> SendAsync(PluginConfig config, NotificationEvent notification, CancellationToken cancellationToken = default);
}
