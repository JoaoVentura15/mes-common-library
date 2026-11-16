export class InterpolationUtilities {

    public static interpolateString(
        template: string,
        params: Record<string, any>,
        start = "{{",
        end = "}}"
    ): string {
        const esc = (s: string) => s.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
        const pattern = new RegExp(`${esc(start)}([^${esc(end[0])}]+)${esc(end)}`, "g");

        return template.replace(pattern, (_, rawKey: string) => {
            const key = rawKey.trim();               // e.g. "material.Id"
            const value = this.getByPath(params, key);    // look up in request body
            return value != null ? String(value) : "";
        });
    }

    public static interpolateObject<T>(template: T, params: Record<string, any>): T {
        if (typeof template === "string") {
            return  this.interpolateString(template, params) as unknown as T;
        }

        if (Array.isArray(template)) {
            return template.map(item =>  this.interpolateObject(item, params)) as unknown as T;
        }

        if (template && typeof template === "object") {
            const result: any = {};
            for (const [k, v] of Object.entries(template)) {
            result[k] =  this.interpolateObject(v as any, params);
            }
            return result;
        }

        return template;
    }

    private static getByPath(obj: any, path: string): any {
        const normalizedPath = path.replace(/\[(\d+)\]/g, ".$1");
        return normalizedPath.split(".").reduce((acc, key) => (acc == null ? undefined : acc[key]), obj);
    }

}