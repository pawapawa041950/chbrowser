// ChBrowser 画像ビューア「画像詳細ペイン」用 JS。
//
// C# (ImageViewerWindow.xaml.cs) からのメッセージ:
//   { type: 'setDetails', url, details }
//     - url      : 表示中の画像 URL (= タブの URL)
//     - details  : null なら「データ無し」表示。値は AiImageMetadata の DTO。
//                  { format, fileSize, width, height, model, positive, negative, parameters, otherMetadata }
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

    /** プロンプト本文を HTML 化する。Contex Loop 等の複数シーン構成で C# が挿入する
     *  区切り見出し行 ("── 共通プレフィックス ──" / "── シーン 1/6: id ──") だけ色を変えて、
     *  本文との境目をひと目で分かるようにする。それ以外は通常のエスケープのみ。 */
    function renderPromptHtml(text) {
        return String(text == null ? '' : text).split('\n').map(function (line) {
            var esc = escapeHtml(line);
            return line.indexOf('──') === 0 ? '<span class="prompt-scene-header">' + esc + '</span>' : esc;
        }).join('\n');
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

    /** AiImageMetadata.HasAiData == false (= 形式は分かったが SD infotext は無い) の場合は基本情報のみ。
     *  AI として解釈できなかった一般メタデータ (EXIF 撮影情報 / PNG コメント等) があればそれも見せる。 */
    function renderBasicOnly(url, d) {
        var html = '<div class="filename">' + escapeHtml(nameFromUrl(url)) + '</div>';
        html += renderInfoSection(d);
        html += renderOtherMetadataSection(d);
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
                    '<div class="prompt-block">' + renderPromptHtml(d.positive) + '</div>' +
                    '</div>';
        }
        if (d.negative) {
            html += '<div class="section">' +
                    '<div class="section-header">ネガティブプロンプト</div>' +
                    '<div class="prompt-block prompt-negative">' + renderPromptHtml(d.negative) + '</div>' +
                    '</div>';
        }
        if (d.parameters && Object.keys(d.parameters).length > 0) {
            html += '<div class="section">' +
                    '<div class="section-header">生成設定</div>' +
                    renderParamsGrid(d.parameters) +
                    '</div>';
        }
        html += renderOtherMetadataSection(d);
        root.innerHTML = html;
    }

    /** AI として解釈できなかった一般メタデータ (C# 側 OtherMetadata) のセクション。
     *  部分ラベル結果 (Generator のみ確定) では生チャンク/EXIF のダンプがここに入る。空なら何も出さない。 */
    function renderOtherMetadataSection(d) {
        var om = d.otherMetadata;
        if (!om || Object.keys(om).length === 0) return '';
        var html = '<div class="section"><div class="section-header">メタデータ</div><div class="params-grid">';
        var keys = Object.keys(om);
        for (var i = 0; i < keys.length; i++) {
            html += '<div class="params-key">' + escapeHtml(keys[i]) + '</div>' +
                    '<div class="params-value">' + escapeHtml(om[keys[i]]) + '</div>';
        }
        html += '</div></div>';
        return html;
    }

    function renderInfoSection(d) {
        var html = '<div class="section section-info"><div class="info-grid">';
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
        // Text encoder / VAE は ComfyUI グラフから取れたときのみ存在する (動画ワークフロー等)。
        var priority = ['Steps', 'Sampler', 'CFG scale', 'Seed', 'Size', 'Model hash', 'Model', 'Text encoder', 'VAE'];
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

    /** 画像の文字の AI 翻訳のセクション (右クリック「AIで翻訳」)。基本情報 (形式・サイズ・生成元等) のすぐ下に入れる
     *  (基本情報が無い表示ではファイル名の下)。t の形と中身の描画は image-translation.js (スレ表示のサムネイルのポップアップと共通)。 */
    function renderTranslation(t) {
        if (!t) return;
        var sec = document.createElement('div');
        sec.className = 'section section-translation';
        sec.innerHTML = '<div class="section-header">翻訳</div>' + window.ChImageTranslation.html(t);
        var after = root.querySelector('.section-info') || root.querySelector('.filename');
        if (after) root.insertBefore(sec, after.nextSibling);   // nextSibling が null なら末尾に入る
        else root.insertBefore(sec, root.firstChild);
    }

    if (window.chrome && window.chrome.webview) {
        window.chrome.webview.addEventListener('message', function (e) {
            var msg = e.data;
            if (!msg || typeof msg !== 'object' || msg.type !== 'setDetails') return;

            // url なし = 「現在タブ無し」 → 空表示
            if (!msg.url) { renderEmpty(); return; }
            // details なし = キャッシュ未ヒット / 非対応形式 → 「取得できません」
            if (!msg.details) renderNoData(msg.url);
            // details あるが AI 情報無し → 基本情報のみ
            else if (!hasAi(msg.details)) renderBasicOnly(msg.url, msg.details);
            // フル表示
            else renderFull(msg.url, msg.details);
            renderTranslation(msg.translation);
        });
    }
})();
