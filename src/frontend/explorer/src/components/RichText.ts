// Documentation text on the page: the safe Markdown subset (markdown.ts) built as elements and text nodes — never as HTML (the CSP
// refuses inline styles too). Links open in a new tab without a referrer or an opener.
import { defineComponent, h, type VNode } from "vue";
import { parseInline, parseMarkdown, type Block, type Inline } from "../markdown.js";

function inlines(nodes: readonly Inline[], links: boolean): (VNode | string)[] {
  return nodes.map((node) => {
    switch (node.kind) {
      case "text":
        return node.text;
      case "code":
        return h("code", node.text);
      case "strong":
        return h("strong", inlines(node.children, links));
      case "em":
        return h("em", inlines(node.children, links));
      case "link":
        // inside a button (a row's summary) a link would be interactive content inside interactive content: its text stays
        return links ? h("a", { href: node.href, target: "_blank", rel: "noopener noreferrer" }, inlines(node.children, links)) : h("span", inlines(node.children, links));
      case "break":
        return h("br");
    }
  });
}

function block(node: Block, links: boolean): VNode {
  switch (node.kind) {
    case "paragraph":
      return h("p", inlines(node.children, links));
    case "code":
      return h("pre", [h("code", node.text)]);
    case "list":
      return h(node.ordered ? "ol" : "ul", node.items.map((item) => h("li", inlines(item, links))));
  }
}

export default defineComponent({
  name: "RichText",
  props: {
    text: { type: String, required: true },
    /** One line of text (a member's or a parameter's): inline markup only, line breaks as spaces. */
    inline: { type: Boolean, default: false },
    /** Links as links; false inside a button, where only their text is shown. */
    links: { type: Boolean, default: true },
  },
  setup(props) {
    return () =>
      props.inline
        ? h("span", { class: "rich-inline" }, inlines(parseInline(props.text.replace(/\s*\n\s*/g, " ")), props.links))
        : h("div", { class: "rich" }, parseMarkdown(props.text).map((b) => block(b, props.links)));
  },
});
