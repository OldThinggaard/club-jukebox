import { Admin } from "./Admin";
import { Hall } from "./Hall";
import { Install } from "./Install";
import { Kiosk } from "./Kiosk";

export default function App() {
  const path = window.location.pathname;
  if (path.startsWith("/player")) return <Hall />;
  if (path.startsWith("/admin")) return <Admin />;
  if (path.startsWith("/install")) return <Install />;
  return <Kiosk />;
}
