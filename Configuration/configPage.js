// JellyDVR Guides settings page (Configuration/configPage.html). jellyfin-web loads this as
// the page's controller (data-controller="__plugin/JellyDvrGuidesJs") and runs it once per
// page, with "viewshow" each time the page is opened.
export default function (view) {
    var pluginUniqueId = "110ae7e3-0c55-4684-b0f8-3e86eb2b53af";
    var page = view;
    var config = null;
    var statuses = [];
    var tuners = [];
    var sd = null;
    var editing = -1;

    function q(sel) { return page.querySelector(sel); }

    function el(tag, cls, text) {
        var e = document.createElement(tag);
        if (cls) { e.className = cls; }
        if (text != null) { e.textContent = text; }
        return e;
    }

    function api(type, path, data) {
        var o = { type: type, url: ApiClient.getUrl("JellyDvrGuides/" + path), dataType: "json" };
        if (data !== undefined) {
            o.data = JSON.stringify(data);
            o.contentType = "application/json";
        }
        return ApiClient.ajax(o);
    }

    function errorText(e) {
        return Promise.resolve(e && e.text ? e.text() : "").then(function (t) { return (t || "").replace(/^"|"$/g, ""); });
    }

    function tunerName(id) {
        var t = tuners.filter(function (x) { return x.Id === id; })[0];
        return t ? t.Name : "a removed tuner";
    }

    function lineupInfo(id) {
        return ((sd && sd.Lineups) || []).filter(function (l) { return l.Lineup === id; })[0];
    }

    function when(utc) {
        return new Date(utc).toLocaleString([], { month: "short", day: "numeric", hour: "numeric", minute: "2-digit" });
    }

    // ---- source cards ----

    function renderSources() {
        var box = q("#Sources");
        box.innerHTML = "";
        if (!config.Sources.length) {
            box.appendChild(el("div", "fieldDescription", "No guide sources yet."));
            box.lastChild.style.marginBottom = "1em";
        }
        config.Sources.forEach(function (s, i) {
            var st = statuses.filter(function (x) { return x.Id === s.Id; })[0] || {};
            var isSd = s.Type === "sd";
            var card = el("div", "jg-card");
            var head = el("div", "jg-head");
            var left = el("div");
            var title = el("div", "jg-title");
            title.appendChild(el("h3", null, s.Name || s.Id));
            title.appendChild(el("span", "jg-tag", isSd ? "SCHEDULES DIRECT" : "XMLTV"));
            if (!isSd && s.Clean) { title.appendChild(el("span", "jg-tag jg-clean", "CLEANED")); }
            left.appendChild(title);
            if (isSd) {
                var li = lineupInfo(s.Lineup);
                left.appendChild(el("div", "jg-sub", "Lineup " + s.Lineup + (li && li.Name ? " · " + li.Name : "") + " · " + s.Days + " days"));
                left.appendChild(el("div", "jg-sub", "No cleaning needed: written by the plugin in the shape Jellyfin reads"));
            } else {
                left.appendChild(el("div", "jg-sub", "From " + (s.Url || "(no address)")));
                var fixes = [];
                if (s.Clean && s.CleanYears) { fixes.push("doubled movie years"); }
                if (s.Clean && s.CleanIds) { fixes.push("Gracenote ids for Jellyfin"); }
                left.appendChild(el("div", "jg-sub", fixes.length ? "Cleaning: " + fixes.join(" · ") : "Passed through as it is"));
            }
            head.appendChild(left);
            var actions = el("div", "jg-actions");
            var edit = el("button", "raised emby-button", "Edit");
            edit.type = "button";
            edit.addEventListener("click", function () { openDialog(i); });
            actions.appendChild(edit);
            var fetch = el("button", "raised emby-button", isSd ? "Write now" : "Fetch now");
            fetch.type = "button";
            fetch.addEventListener("click", function () {
                fetch.disabled = true;
                fetch.textContent = isSd ? "Writing..." : "Fetching...";
                api("POST", "Refresh/" + encodeURIComponent(s.Id)).then(function (all) {
                    statuses = all || [];
                    renderSources();
                }).catch(function (e) {
                    errorText(e).then(function (t) { q("#ApplyResult").textContent = "Fetch failed. " + t; });
                    renderSources();
                });
            });
            actions.appendChild(fetch);
            head.appendChild(actions);
            card.appendChild(head);

            var rows = el("div", "jg-rows");
            function row(label, value, cls) {
                rows.appendChild(el("span", null, label));
                rows.appendChild(el("span", cls || null, value));
            }
            row("Address for Jellyfin", st.Address || ApiClient.getUrl("JellyDvrGuides/guide/" + s.Id + ".xml"));
            var p = st.Provider;
            row("In Jellyfin", !p || !p.InJellyfin ? "Not added yet" + (s.ManageProvider ? "" : " (Edit to let the plugin add it)")
                : "Guide provider for " + (p.AllTuners ? "all tuners (limit it to its own tuners)" : p.Tuners.length ? p.Tuners.map(tunerName).join(", ") : "no tuners"));
            ((p && p.Others) || []).forEach(function (o) {
                rows.appendChild(el("span", null, "Also for these tuners"));
                var cell = el("span", o.ComesFirst ? "jg-err" : null,
                    (o.Type === "xmltv" ? "Guide provider " + o.Where : o.Where) + " (" +
                    (o.AllTuners ? "all tuners" : o.Tuners.map(tunerName).join(", ")) + ")" +
                    (o.ComesFirst ? ": Jellyfin uses it instead of this guide. " : ". "));
                var remove = el("button", "raised emby-button", "Remove it");
                remove.type = "button";
                remove.style.margin = ".3em 0 0";
                remove.style.display = "block";
                remove.addEventListener("click", function () {
                    if (remove.dataset.armed !== "1") {
                        remove.dataset.armed = "1";
                        remove.textContent = "Remove it from Jellyfin?";
                        return;
                    }
                    remove.disabled = true;
                    api("POST", "Providers/" + encodeURIComponent(o.Id) + "/Remove").then(function (all) {
                        statuses = all || [];
                        q("#ApplyResult").textContent = "Removed the guide provider " + o.Where + ".\nJellyfin's Refresh Guide is running.";
                        renderSources();
                    }).catch(function (e) {
                        errorText(e).then(function (t) { q("#ApplyResult").textContent = "Couldn't remove it. " + t; });
                        renderSources();
                    });
                });
                cell.appendChild(remove);
                rows.appendChild(cell);
            });
            var f = st.Fetch;
            if (f && f.FetchedUtc) {
                var bits = [when(f.FetchedUtc), f.Programmes ? f.Programmes.toLocaleString() + " programmes" : "saved copy"];
                if (f.ChannelsInLineup) { bits.splice(1, 0, f.Channels + " of " + f.ChannelsInLineup + " channels"); }
                if (f.TitlesCleaned) { bits.push(f.TitlesCleaned.toLocaleString() + " years removed"); }
                if (f.ProgramIdsAdded) { bits.push(f.ProgramIdsAdded.toLocaleString() + " ids added"); }
                row(isSd ? "Guide written" : "Last fetch", bits.join(" · "));
            } else {
                row(isSd ? "Guide written" : "Last fetch", "Not since the server started");
            }
            if (f && f.LastError) {
                row("Last error", when(f.LastErrorUtc) + ": " + f.LastError, "jg-err");
            }
            card.appendChild(rows);
            box.appendChild(card);
        });
    }

    // ---- add / edit dialog ----

    function slug(text) {
        return (text || "").toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-+|-+$/g, "").slice(0, 40);
    }

    function showType() {
        var isSd = q("#SrcType").value === "sd";
        q("#SrcXmltv").hidden = isSd;
        q("#SrcSd").hidden = !isSd;
        q("#SrcCleanOptions").hidden = !q("#SrcClean").checked;
        q("#SrcAddress").textContent = ApiClient.getUrl("JellyDvrGuides/guide/" + (q("#SrcId").value || "…") + ".xml");
        q("#SrcOnlyTunerRow").hidden = !isSd;
    }

    function openDialog(index) {
        editing = index;
        var s = index >= 0 ? config.Sources[index] : {
            Id: "", Name: "", Type: "xmltv", Url: "", Lineup: "", Days: 14,
            Clean: true, CleanYears: true, CleanIds: true, ManageProvider: true, OnlyTunerChannels: true, Tuners: []
        };
        q("#SourceDialogTitle").textContent = index >= 0 ? "Edit guide source" : "Add guide source";
        q("#SrcName").value = s.Name || "";
        q("#SrcType").value = s.Type === "sd" ? "sd" : "xmltv";
        q("#SrcUrl").value = s.Url || "";
        q("#SrcClean").checked = s.Clean !== false;
        q("#SrcCleanYears").checked = s.CleanYears !== false;
        q("#SrcCleanIds").checked = s.CleanIds !== false;
        q("#SrcDays").value = s.Days || 14;
        q("#SrcId").value = s.Id || "";
        q("#SrcId").dataset.auto = index >= 0 ? "" : "1";
        q("#SrcManage").checked = !!s.ManageProvider;
        q("#SrcOnlyTuner").checked = s.OnlyTunerChannels !== false;

        var lineup = q("#SrcLineup");
        lineup.innerHTML = "";
        var known = ((sd && sd.Lineups) || []).slice();
        if (s.Lineup && !known.some(function (l) { return l.Lineup === s.Lineup; })) {
            known.push({ Lineup: s.Lineup, Name: "(not on the account)" });
        }
        if (!known.length) {
            lineup.appendChild(el("option", null, "Sign in and Update now to list your lineups"));
            lineup.lastChild.value = "";
        }
        known.forEach(function (l) {
            var o = el("option", null, l.Lineup + (l.Name ? " · " + l.Name : "") + (l.Transport ? " (" + l.Transport + ")" : ""));
            o.value = l.Lineup;
            lineup.appendChild(o);
        });
        lineup.value = s.Lineup || (known[0] ? known[0].Lineup : "");

        var tbox = q("#SrcTuners");
        tbox.innerHTML = "";
        if (!tuners.length) { tbox.appendChild(el("span", "fieldDescription", "No tuners in Jellyfin.")); }
        tuners.forEach(function (t) {
            var label = el("label", "jg-check");
            var c = document.createElement("input");
            c.type = "checkbox";
            c.value = t.Id;
            c.checked = (s.Tuners || []).indexOf(t.Id) >= 0;
            label.appendChild(c);
            label.appendChild(el("span", null, t.Name));
            tbox.appendChild(label);
        });

        q("#SrcDelete").hidden = index < 0;
        q("#SrcError").textContent = "";
        showType();
        q("#SourceDialog").hidden = false;
        q("#SrcName").focus();
    }

    function closeDialog() {
        q("#SourceDialog").hidden = true;
    }

    function dialogSource() {
        var old = editing >= 0 ? config.Sources[editing] : {};
        var isSd = q("#SrcType").value === "sd";
        return {
            Id: q("#SrcId").value.trim().toLowerCase(),
            Name: q("#SrcName").value.trim(),
            Type: isSd ? "sd" : "xmltv",
            Url: isSd ? "" : q("#SrcUrl").value.trim(),
            Lineup: isSd ? q("#SrcLineup").value : "",
            Days: Math.min(21, Math.max(1, parseInt(q("#SrcDays").value, 10) || 14)),
            Clean: !isSd && q("#SrcClean").checked,
            CleanYears: q("#SrcCleanYears").checked,
            CleanIds: q("#SrcCleanIds").checked,
            ManageProvider: q("#SrcManage").checked,
            OnlyTunerChannels: q("#SrcOnlyTuner").checked,
            Tuners: Array.prototype.filter.call(q("#SrcTuners").querySelectorAll("input"), function (c) { return c.checked; })
                .map(function (c) { return c.value; }),
            ProviderId: old.ProviderId || ""
        };
    }

    function validate(s) {
        if (!s.Name) { return "Give the source a name."; }
        if (!/^[a-z0-9][a-z0-9-]*$/.test(s.Id)) { return "The address name takes lower-case letters, digits and dashes."; }
        if (config.Sources.some(function (x, i) { return i !== editing && x.Id.toLowerCase() === s.Id; })) { return "Another source already uses that address name."; }
        if (s.Type === "xmltv" && !/^https?:\/\//i.test(s.Url)) { return "Enter the guide's address (http://…)."; }
        if (s.Type === "sd" && !s.Lineup) { return "Pick a lineup (sign in and Update now to list them)."; }
        if (s.Type === "sd" && s.OnlyTunerChannels && !s.Tuners.length) { return "Tick the tuners this guide is for (or untick \"Leave out channels these tuners don't have\")."; }
        if (s.ManageProvider && !s.Tuners.length) { return "Tick the tuners the guide is for (a guide for all tuners gives each tuner's channels the wrong guide)."; }
        return null;
    }

    // Saves the sources (from the latest config, so nothing the server set is lost) and
    // then lets the plugin bring Jellyfin's guide providers in line.
    function saveSources(sources) {
        Dashboard.showLoadingMsg();
        return ApiClient.getPluginConfiguration(pluginUniqueId).then(function (fresh) {
            fresh.Sources = sources;
            return ApiClient.updatePluginConfiguration(pluginUniqueId, fresh);
        }).then(function () {
            return api("POST", "Providers/Apply").then(function (changes) {
                q("#ApplyResult").textContent = (changes || []).length ? changes.join("\n") + "\nJellyfin's Refresh Guide is running." : "";
            }).catch(function (e) {
                return errorText(e).then(function (t) { q("#ApplyResult").textContent = t || "Couldn't update Jellyfin's guide providers."; });
            });
        }).then(reload).finally(function () { Dashboard.hideLoadingMsg(); });
    }

    q("#SrcType").addEventListener("change", function () {
        if (q("#SrcType").value === "sd") { q("#SrcClean").checked = false; }
        showType();
    });
    q("#SrcClean").addEventListener("change", showType);
    q("#SrcManage").addEventListener("change", showType);
    q("#SrcOnlyTuner").addEventListener("change", showType);
    q("#SrcName").addEventListener("input", function () {
        if (q("#SrcId").dataset.auto) {
            q("#SrcId").value = slug(q("#SrcName").value);
            showType();
        }
    });
    q("#SrcId").addEventListener("input", function () {
        q("#SrcId").dataset.auto = "";
        showType();
    });
    q("#SrcCancel").addEventListener("click", closeDialog);
    q("#SourceDialog").addEventListener("keydown", function (e) { if (e.key === "Escape") { closeDialog(); } });
    q("#SrcSave").addEventListener("click", function () {
        var s = dialogSource();
        var problem = validate(s);
        if (problem) {
            q("#SrcError").textContent = problem;
            return;
        }
        var sources = config.Sources.slice();
        if (editing >= 0) { sources[editing] = s; } else { sources.push(s); }
        closeDialog();
        saveSources(sources);
    });
    q("#SrcDelete").addEventListener("click", function () {
        var s = config.Sources[editing];
        var managed = s.ProviderId && (config.ManagedProviderIds || []).indexOf(s.ProviderId) >= 0;
        q("#SrcError").textContent = "";
        if (q("#SrcDelete").dataset.armed !== "1") {
            q("#SrcDelete").dataset.armed = "1";
            q("#SrcDelete").querySelector("span").textContent = "Delete " + (managed ? "it and its guide provider" : "it") + "?";
            return;
        }
        q("#SrcDelete").dataset.armed = "";
        q("#SrcDelete").querySelector("span").textContent = "Delete source";
        var sources = config.Sources.filter(function (x, i) { return i !== editing; });
        closeDialog();
        saveSources(sources);
    });
    q("#AddSource").addEventListener("click", function () { openDialog(-1); });

    // ---- Schedules Direct ----

    function showSd(s) {
        sd = s;
        var out = q("#SdStatus");
        q("#SdAccountResult").textContent = s && s.Username ? "Signed in as " + s.Username + "." : "No account saved.";
        if (!s) {
            out.textContent = "";
            return;
        }
        var lines = [];
        var u = s.Update;
        if (u && u.FinishedUtc) {
            lines.push("Last update " + when(u.FinishedUtc) + " (" + Math.round(u.DurationMs / 1000) + " s): " +
                u.Lineups + " lineup(s) in use, " + u.Stations + " stations; " + u.DaysFetched + " changed days and " + u.ProgramsFetched + " programmes downloaded.");
        }
        if (u && u.Error) { lines.push("Last error (" + when(u.ErrorUtc) + "): " + u.Error); }
        if (s.Blocked) { lines.push("Paused " + s.Blocked); }
        if (s.ImagesBlocked) { lines.push("Pictures: " + s.ImagesBlocked); }
        if (!lines.length) { lines.push("No update since the server started."); }
        var used = (config ? config.Sources : []).filter(function (x) { return x.Type === "sd"; }).map(function (x) { return x.Lineup; });
        var ls = s.Lineups || [];
        if (ls.length) {
            lines.push("Lineups on the account: " + ls.map(function (l) {
                return l.Lineup + (used.indexOf(l.Lineup) >= 0 ? " (in use)" : "");
            }).join(" · "));
        }
        out.textContent = lines.join("\n");
    }

    q("#SdSignIn").addEventListener("click", function () {
        var out = q("#SdAccountResult");
        out.textContent = "Signing in...";
        api("POST", "SchedulesDirect/Account", { Username: q("#SdUsername").value, Password: q("#SdPassword").value })
            .then(function (text) {
                q("#SdPassword").value = "";
                out.textContent = text || "Signed in.";
            }).catch(function (e) {
                errorText(e).then(function (t) { out.textContent = "Sign-in failed. " + t; });
            });
    });

    q("#SdUpdate").addEventListener("click", function () {
        q("#SdStatus").textContent = "Updating from Schedules Direct... (the first time takes a minute or two)";
        api("POST", "SchedulesDirect/Update").then(function (s) {
            showSd(s);
            renderSources();
        }).catch(function () { q("#SdStatus").textContent = "Update request failed."; });
    });

    // ---- load / save ----

    function reload() {
        return Promise.all([
            ApiClient.getPluginConfiguration(pluginUniqueId),
            api("GET", "Status").catch(function () { return []; }),
            api("GET", "Tuners").catch(function () { return []; }),
            api("GET", "SchedulesDirect/Status").catch(function () { return null; })
        ]).then(function (r) {
            config = r[0];
            config.Sources = config.Sources || [];
            statuses = r[1] || [];
            tuners = r[2] || [];
            q("#LocalNetworkOnly").checked = config.LocalNetworkOnly !== false;
            q("#MinRefetchMinutes").value = config.MinRefetchMinutes != null ? config.MinRefetchMinutes : 10;
            q("#UpstreamTimeoutSeconds").value = config.UpstreamTimeoutSeconds || 120;
            q("#RecheckPictureDays").value = config.RecheckPictureDays != null ? config.RecheckPictureDays : 14;
            q("#SdUsername").value = config.SdUsername || "";
            showSd(r[3]);
            renderSources();
        });
    }

    page.addEventListener("viewshow", function () {
        Dashboard.showLoadingMsg();
        reload().finally(function () { Dashboard.hideLoadingMsg(); });
    });

    q("#JellyDvrGuidesConfigForm").addEventListener("submit", function (e) {
        e.preventDefault();
        Dashboard.showLoadingMsg();
        ApiClient.getPluginConfiguration(pluginUniqueId).then(function (fresh) {
            fresh.LocalNetworkOnly = q("#LocalNetworkOnly").checked;
            fresh.MinRefetchMinutes = parseInt(q("#MinRefetchMinutes").value, 10) || 0;
            fresh.UpstreamTimeoutSeconds = parseInt(q("#UpstreamTimeoutSeconds").value, 10) || 120;
            var days = parseInt(q("#RecheckPictureDays").value, 10);
            fresh.RecheckPictureDays = isNaN(days) || days < 0 ? 14 : days;
            return ApiClient.updatePluginConfiguration(pluginUniqueId, fresh);
        }).then(function (result) {
            Dashboard.processPluginConfigurationUpdateResult(result);
            return reload();
        }).finally(function () { Dashboard.hideLoadingMsg(); });
        return false;
    });
}
