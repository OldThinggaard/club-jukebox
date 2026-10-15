import { Admin } from "./Admin";
import { Hall } from "./Hall";
import { Install } from "./Install";
import { Kiosk } from "./Kiosk";

export default function App() {
  const path = window.location.pathname;
  const openedBySwipe = performance.getEntriesByType("navigation").some((entry) => entry instanceof PerformanceNavigationTiming && entry.type === "back_forward");
  if (openedBySwipe && path.startsWith("/admin")) {
    window.location.replace("/");
    return null;
  }
  if (path.startsWith("/player")) return <Hall />;
  if (path.startsWith("/admin")) return <Admin />;
  if (path.startsWith("/install")) return <Install />;
  return <Kiosk />;
}
