// ChBrowser 画像ビューア「画像詳細ペイン」用 JS。
//
// C# (ImageViewerWindow.xaml.cs) からのメッセージ:
//   { type: 'setDetails', url, details }
//     - url      : 表示中の画像 URL (= タブの URL)
//     - details  : null なら「データ無し」表示。値は AiImageMetadata の DTO。
//                  { format, fileSize, width, height, model, positive, negative, parameters }
//
// SD WebUI infotext のパースは C# 側 (AiImageMetadataService) で済んでいる前提。
// JS 側は表示専用 (= 参考 viewer の file-details.js renderImageDetails 相当を移植したもの)。

(function () {
    'use strict';

    var root = document.getElementById('root');

    function escapeHtml(text) {
        if (text == null) return '';
        var div = document.createElement('div');
        div.textContent = String(text);
        return div.innerHTML;
    }

    function formatFileSize(bytes) {
        if (bytes == null || isNaN(bytes)) return '-';
        if (bytes < 1024) return bytes + ' B';
        if (bytes < 1024 * 1024) return (bytes / 1024).toFixed(1) + ' KB';
        if (bytes < 1024 * 1024 * 1024) return (bytes / (1024 * 1024)).toFixed(2) + ' MB';
        return (bytes / (1024 * 1024 * 1024)).toFixed(2) + ' GB';
    }

    /** URL からファイル名らしき短い文字列を抽出。? とか # は捨てる。 */
    function nameFromUrl(url) {
        if (!url) return '';
        try {
            var u = new URL(url);
            var p = u.pathname.split('/').filter(Boolean);
            return p[p.length - 1] || u.hostname;
        } catch (e) {
            return url;
        }
    }

    /** 何も表示するものがないとき (タブ無し / 起動直後)。 */
    function renderEmpty() {
        root.innerHTML = '<div class="empty-state">画像詳細</div>';
    }

    /** タブはあるが、画像がまだキャッシュに無い / 解析できない / 対応形式外の場合。 */
    function renderNoData(url) {
        root.innerHTML =
            '<div class="filename">' + escapeHtml(nameFromUrl(url)) + '</div>' +
            '<div class="empty-state">この画像の詳細情報を取得できません</div>';
    }

    /** AiImageMetadata.HasAiData == false (= 形式は分かったが SD infotext は無い) の場合は基本情報のみ。 */
    function renderBasicOnly(url, d) {
        var html = '<div class="filename">' + escapeHtml(nameFromUrl(url)) + '</div>';
        html += renderInfoSection(d);
        html += '<div class="empty-state">AI 生成情報は含まれていません</div>';
        root.innerHTML = html;
    }

    /** SD WebUI infotext がパースできた場合の full version。 */
    function renderFull(url, d) {
        var html = '<div class="filename">' + escapeHtml(nameFromUrl(url)) + '</div>';
        html += renderInfoSection(d);

        if (d.model) {
            html += '<div class="section">' +
                    '<div class="section-header">モデル</div>' +
                    '<div class="info-value">' + escapeHtml(d.model) + '</div>' +
                    '</div>';
        }
        if (d.positive) {
            html += '<div class="section">' +
                    '<div class="section-header">プロンプト</div>' +
                    '<div class="prompt-block">' + escapeHtml(d.positive) + '</div>' +
                    '</div>';
        }
        if (d.negative) {
            html += '<div class="section">' +
                    '<div class="section-header">ネガティブプロンプト</div>' +
                    '<div class="prompt-block prompt-negative">' + escapeHtml(d.negative) + '</div>' +
                    '</div>';
        }
        if (d.parameters && Object.keys(d.parameters).length > 0) {
            html += '<div class="section">' +
                    '<div class="section-header">生成設定</div>' +
                    renderParamsGrid(d.parameters) +
                    '</div>';
        }
        root.innerHTML = html;
    }

    function renderInfoSection(d) {
        var html = '<div class="section"><div class="info-grid">';
        if (d.format)   html += '<div class="info-label">形式</div><div class="info-value">' + escapeHtml(d.format) + '</div>';
                        html += '<div class="info-label">サイズ</div><div class="info-value">' + formatFileSize(d.fileSize) + '</div>';
        if (d.width && d.height)
                        html += '<div class="info-label">画像サイズ</div><div class="info-value">' + d.width + ' × ' + d.height + '</div>';
        // 「生成元」(= ComfyUI / SD WebUI Forge / 等) を file-details.js の検出ロジック移植版で表示。
        // 視認性のため info-value に highlight クラスを付ける。
        if (d.generator)
                        html += '<div class="info-label">生成元</div><div class="info-value info-generator">' + escapeHtml(d.generator) + '</div>';
        html += '</div></div>';
        return html;
    }

    function renderParamsGrid(params) {
        // file-details.js と同じ priority 順 (人間が見たい主要パラメータを先頭に)。
        var priority = ['Steps', 'Sampler', 'CFG scale', 'Seed', 'Size', 'Model hash', 'Model'];
        var ordered  = [];
        for (var i = 0; i < priority.length; i++) {
            if (params[priority[i]] !== undefined) ordered.push(priority[i]);
        }
        var keys = Object.keys(params);
        for (var j = 0; j < keys.length; j++) {
            // Generator は画像情報グリッドの方で表示するので params グリッドからは除く (重複防止)。
            if (keys[j] === 'Generator') continue;
            if (ordered.indexOf(keys[j]) < 0) ordered.push(keys[j]);
        }

        var html = '<div class="params-grid">';
        for (var k = 0; k < ordered.length; k++) {
            var key   = ordered[k];
            var value = params[key];
            html += '<div class="params-key">' + escapeHtml(key) + '</div>' +
                    '<div class="params-value">' + escapeHtml(value) + '</div>';
        }
        html += '</div>';
        return html;
    }

    function hasAi(d) {
        if (!d) return false;
        if (d.model || d.positive || d.negative) return true;
        return d.parameters && Object.keys(d.parameters).length > 0;
    }

    if (window.chrome && window.chrome.webview) {
        window.chrome.webview.addEventListener('message', function (e) {
            var msg = e.data;
            if (!msg || typeof msg !== 'object' || msg.type !== 'setDetails') return;

            // url なし = 「現在タブ無し」 → 空表示
            if (!msg.url) { renderEmpty(); return; }
            // details なし = キャッシュ未ヒット / 非対応形式 → 「取得できません」
            if (!msg.details) { renderNoData(msg.url); return; }
            // details あるが AI 情報無し → 基本情報のみ
            if (!hasAi(msg.details)) { renderBasicOnly(msg.url, msg.details); return; }
            // フル表示
            renderFull(msg.url, msg.details);
        });
    }
})();
