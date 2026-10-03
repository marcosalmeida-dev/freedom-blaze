const preferenceCookie = "satsConverterPreference";

export function readPreferences() {
    const cookies = new Map(document.cookie.split(";").map(cookie => {
        const separator = cookie.indexOf("=");
        return [cookie.slice(0, separator).trim(), cookie.slice(separator + 1)];
    }));

    const saved = cookies.get(preferenceCookie);
    if (saved) {
        try {
            const preferences = JSON.parse(decodeURIComponent(saved));
            if (preferences && typeof preferences.direction === "string"
                && (preferences.amount === null || typeof preferences.amount === "string")
                && (preferences.cultureName === null || typeof preferences.cultureName === "string")) {
                return preferences;
            }
        } catch {
            // Ignore damaged/old preferences and keep the converter usable.
        }
    }

    const direction = cookies.get("lastConversionTypeCookie");
    if (direction === "BitcoinToCurrency" || direction === "CurrencyToBitcoin") {
        return {
            direction,
            amount: cookies.get(direction === "CurrencyToBitcoin" ? "currencyValueCookie" : "satsValueCookie") || null,
            cultureName: null
        };
    }
    return null;
}

export function savePreferences(preferences) {
    const value = encodeURIComponent(JSON.stringify(preferences));
    document.cookie = `${preferenceCookie}=${value}; max-age=2592000; path=/; SameSite=Lax${location.protocol === "https:" ? "; Secure" : ""}`;
}

export function formatTime(utcMilliseconds, locale) {
    return new Intl.DateTimeFormat(locale || undefined, {
        hour: "2-digit",
        minute: "2-digit"
    }).format(new Date(utcMilliseconds));
}
