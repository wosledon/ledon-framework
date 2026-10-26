// 覆盖 docfx 默认逻辑：docs/toc.yml 位于内容根目录时会同时被当作顶部导航和侧边目录，
// 默认实现因此会关闭侧边目录。这里保留侧边目录（landing 首页除外）。
var common = require('./common.js');
var extension = require('./conceptual.extension.js');

exports.transform = function (model) {
  if (extension && extension.preTransform) {
    model = extension.preTransform(model);
  }

  model._disableToc = !model._tocPath || model._layout === 'landing';
  model.docurl = model.docurl || common.getImproveTheDocHref(model, model._gitContribute, model._gitUrlPattern);

  if (extension && extension.postTransform) {
    model = extension.postTransform(model);
  }

  return model;
};
