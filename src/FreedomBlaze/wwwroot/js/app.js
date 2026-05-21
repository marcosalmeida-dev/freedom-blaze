window.blazorCulture = {
    get: () => window.localStorage['BlazorCulture'],
    set: (value) => window.localStorage['BlazorCulture'] = value
};

window.cookieHelper = {
    setCookie: function (name, value, days) {
        var expires = "";
        if (days) {
            var date = new Date();
            date.setTime(date.getTime() + (days * 24 * 60 * 60 * 1000));
            expires = "; expires=" + date.toUTCString();
        }
        document.cookie = name + "=" + (value || "") + expires + "; path=/";
    },
    getCookie: function (name) {
        var nameEQ = name + "=";
        var ca = document.cookie.split(';');
        for (var i = 0; i < ca.length; i++) {
            var c = ca[i];
            while (c.charAt(0) === ' ') c = c.substring(1, c.length);
            if (c.indexOf(nameEQ) === 0) return c.substring(nameEQ.length, c.length);
        }
        return null;
    }
};

window.getUserEnvironment = function () {
    return {
        language: navigator.language,
        platform: navigator.platform,
        userAgent: navigator.userAgent
    };
};

window.darkModeHelper = {
    getSystemPreference: function () {
        return window.matchMedia('(prefers-color-scheme: dark)').matches;
    },
    watchSystemPreference: function (dotNetHelper) {
        const mq = window.matchMedia('(prefers-color-scheme: dark)');
        mq.addEventListener('change', (e) => {
            dotNetHelper.invokeMethodAsync('SystemPreferenceChanged', e.matches);
        });
    }
};

window.formatLocalTime = function (utcMs, locale) {
    return new Intl.DateTimeFormat(locale, {
        hour: '2-digit',
        minute: '2-digit',
        second: '2-digit'
    }).format(new Date(utcMs));
};

window.scrollToElement = (elementId) => {
    const el = document.getElementById(elementId);
    if (el) {
        el.scrollIntoView({ behavior: 'smooth', block: 'start' });
    }
};
